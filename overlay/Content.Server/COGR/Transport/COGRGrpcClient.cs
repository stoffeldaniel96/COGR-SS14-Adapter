using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using COGR.Core.Identifiers;
using CoreSourceSequence = COGR.Core.Sequences.SourceSequence;
using COGR.Core.Time;
using Proto = COGR.Transport.Grpc.Protocol.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Transport;

/// <summary>
/// Owns the real gRPC AttachWorld duplex stream to the COGR runtime.
/// The request stream has one writer task and the response stream has one reader task.
/// </summary>
public sealed class COGRGrpcClient : IAsyncDisposable
{
    private readonly string _endpoint;
    private readonly ISawmill _sawmill;
    private readonly Channel<Proto.EnvironmentEnvelope> _outgoing = Channel.CreateBounded<Proto.EnvironmentEnvelope>(
        new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private GrpcChannel? _channel;
    private Proto.RuntimeService.RuntimeServiceClient? _client;
    private AsyncDuplexStreamingCall<Proto.EnvironmentEnvelope, Proto.RuntimeEnvelope>? _stream;
    private CancellationTokenSource? _streamCts;
    private Task? _sendLoop;
    private Task? _receiveLoop;
    private bool _disposed;

    public COGRGrpcClient(string endpoint, ISawmill sawmill)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _sawmill = sawmill ?? throw new ArgumentNullException(nameof(sawmill));
    }

    public bool IsConnected => _stream != null && _streamCts is { IsCancellationRequested: false };

    public event Action<Proto.RuntimeEnvelope>? MessageReceived;
    public event Action<Exception?>? Disconnected;

    public async Task<COGRHandshakeResult> ConnectAsync(
        WorldId worldId,
        ConnectionId connectionId,
        Func<CoreSourceSequence> nextSourceSequence,
        Func<SimTick> currentTick,
        string adapterVersion,
        string ss14Revision,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(COGRGrpcClient));
        if (IsConnected)
            return COGRHandshakeResult.Failed("A duplex stream is already active.");

        ArgumentNullException.ThrowIfNull(nextSourceSequence);
        ArgumentNullException.ThrowIfNull(currentTick);

        var timing = IoCManager.Resolve<IGameTiming>();
        var experiencedTicksPerSecond = checked((uint)timing.TickRate);
        ArgumentOutOfRangeException.ThrowIfZero(experiencedTicksPerSecond);

        try
        {
            _channel = GrpcChannel.ForAddress(_endpoint, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                },
            });
            _client = new Proto.RuntimeService.RuntimeServiceClient(_channel);
            _streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stream = _client.AttachWorld(cancellationToken: _streamCts.Token);

            await _stream.RequestStream.WriteAsync(
                CreateAttachRequest(worldId, connectionId, nextSourceSequence(), currentTick(), adapterVersion, ss14Revision),
                cancellationToken).ConfigureAwait(false);

            var runtimeHandshake = await ReadHandshakeMessageAsync(cancellationToken).ConfigureAwait(false);
            if (runtimeHandshake.PayloadCase == Proto.RuntimeEnvelope.PayloadOneofCase.ConnectionRejected)
                return await RejectAndCleanupAsync(runtimeHandshake.ConnectionRejected.Details).ConfigureAwait(false);
            if (runtimeHandshake.PayloadCase != Proto.RuntimeEnvelope.PayloadOneofCase.RuntimeHandshake)
                return await RejectAndCleanupAsync($"Expected RuntimeHandshake, received {runtimeHandshake.PayloadCase}.").ConfigureAwait(false);
            if (runtimeHandshake.RuntimeHandshake.ProtocolVersion?.Major != 1)
                return await RejectAndCleanupAsync("Runtime protocol major version is incompatible.").ConfigureAwait(false);

            await _stream.RequestStream.WriteAsync(
                CreateAdapterHandshake(
                    worldId,
                    connectionId,
                    nextSourceSequence(),
                    currentTick(),
                    experiencedTicksPerSecond,
                    adapterVersion,
                    ss14Revision),
                cancellationToken).ConfigureAwait(false);

            var accepted = await ReadHandshakeMessageAsync(cancellationToken).ConfigureAwait(false);
            if (accepted.PayloadCase == Proto.RuntimeEnvelope.PayloadOneofCase.ConnectionRejected)
                return await RejectAndCleanupAsync(accepted.ConnectionRejected.Details).ConfigureAwait(false);
            if (accepted.PayloadCase != Proto.RuntimeEnvelope.PayloadOneofCase.ConnectionAccepted)
                return await RejectAndCleanupAsync($"Expected ConnectionAccepted, received {accepted.PayloadCase}.").ConfigureAwait(false);
            if (!MatchesContext(accepted, worldId, connectionId))
                return await RejectAndCleanupAsync("ConnectionAccepted context did not match the requested world and connection.").ConfigureAwait(false);

            _sendLoop = RunSendLoopAsync(_streamCts.Token);
            _receiveLoop = RunReceiveLoopAsync(_streamCts.Token);

            _sawmill.Info(
                "Established COGR AttachWorld stream for world {0}, connection {1}",
                worldId,
                connectionId);

            return COGRHandshakeResult.Success(
                worldId.ToGuid(),
                connectionId.ToGuid(),
                accepted.ConnectionAccepted.CurrentTick,
                accepted.RuntimeSequence?.Value ?? 0);
        }
        catch (RpcException rpcEx) when (IsTransientStatus(rpcEx.StatusCode))
        {
            // Transient failures (runtime not ready, network hiccup) are expected during startup
            _sawmill.Info(
                "COGR runtime handshake not ready ({0}); retry scheduled.",
                rpcEx.StatusCode);
            await CleanupAsync().ConfigureAwait(false);
            return COGRHandshakeResult.Transient(rpcEx.Message, rpcEx.StatusCode.ToString());
        }
        catch (RpcException rpcEx)
        {
            // Non-transient gRPC errors (protocol issues, rejected connections)
            _sawmill.Error(
                "COGR handshake rejected or failed ({0}): {1}",
                rpcEx.StatusCode,
                rpcEx.Message);
            await CleanupAsync().ConfigureAwait(false);
            return COGRHandshakeResult.Failed(rpcEx.Message);
        }
        catch (Exception ex)
        {
            _sawmill.Error("Failed to establish COGR duplex stream: {0}", ex.Message);
            await CleanupAsync().ConfigureAwait(false);
            return COGRHandshakeResult.Failed(ex.Message);
        }
    }

    private static bool IsTransientStatus(StatusCode statusCode) =>
        statusCode is StatusCode.Unavailable
            or StatusCode.DeadlineExceeded
            or StatusCode.Aborted
            or StatusCode.ResourceExhausted;

    public ValueTask SendAsync(Proto.EnvironmentEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsConnected)
            return ValueTask.FromException(new InvalidOperationException("COGR duplex stream is not connected."));
        return _outgoing.Writer.WriteAsync(envelope, cancellationToken);
    }

    public async Task<bool> SendHeartbeatAsync(
        ConnectionId connectionId,
        SimTick tick,
        ulong sequence,
        CancellationToken cancellationToken = default)
    {
        if (_client == null || !IsConnected)
            return false;

        try
        {
            await _client.PingAsync(
                new Proto.Heartbeat
                {
                    ConnectionId = new Proto.ConnectionId
                    {
                        Value = connectionId.ToGuid().ToString("D"),
                    },
                    SenderTick = tick.Value,
                    Sequence = sequence,
                },
                cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Warning("COGR heartbeat failed: {0}", ex.Message);
            return false;
        }
    }

    public async Task DisconnectAsync(
        ConnectionId connectionId,
        string reason = "Normal shutdown",
        CancellationToken cancellationToken = default)
    {
        if (_client != null && IsConnected)
        {
            try
            {
                await _client.DisconnectAsync(
                    new Proto.DisconnectNotice
                    {
                        ConnectionId = new Proto.ConnectionId
                        {
                            Value = connectionId.ToGuid().ToString("D"),
                        },
                        Reason = Proto.DisconnectReason.Normal,
                        Message = reason,
                    },
                    cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _sawmill.Warning("COGR disconnect notice failed: {0}", ex.Message);
            }
        }

        await CleanupAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await CleanupAsync().ConfigureAwait(false);
        _disposed = true;
    }

    private async Task<Proto.RuntimeEnvelope> ReadHandshakeMessageAsync(CancellationToken cancellationToken)
    {
        if (_stream == null || !await _stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            throw new RpcException(new Status(StatusCode.Unavailable, "Runtime closed the stream during handshake."));
        return _stream.ResponseStream.Current;
    }

    private async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var envelope in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_stream == null)
                    break;
                await _stream.RequestStream.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HandleTransportFailure(ex);
        }
    }

    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_stream == null)
                return;

            while (await _stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                MessageReceived?.Invoke(_stream.ResponseStream.Current);

            HandleTransportFailure(null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HandleTransportFailure(ex);
        }
    }

    private void HandleTransportFailure(Exception? exception)
    {
        if (_streamCts is not { IsCancellationRequested: false })
            return;

        _streamCts.Cancel();
        Disconnected?.Invoke(exception);
    }

    private async Task<COGRHandshakeResult> RejectAndCleanupAsync(string error)
    {
        await CleanupAsync().ConfigureAwait(false);
        return COGRHandshakeResult.Failed(error);
    }

    private async Task CleanupAsync()
    {
        if (_streamCts != null && !_streamCts.IsCancellationRequested)
            await _streamCts.CancelAsync().ConfigureAwait(false);

        _outgoing.Writer.TryComplete();

        if (_stream != null)
        {
            try
            {
                await _stream.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            _stream.Dispose();
            _stream = null;
        }

        var currentTaskId = Task.CurrentId;
        if (_sendLoop != null && _sendLoop.Id != currentTaskId)
            await IgnoreCancellationAsync(_sendLoop).ConfigureAwait(false);
        if (_receiveLoop != null && _receiveLoop.Id != currentTaskId)
            await IgnoreCancellationAsync(_receiveLoop).ConfigureAwait(false);

        _sendLoop = null;
        _receiveLoop = null;
        _client = null;
        _channel?.Dispose();
        _channel = null;
        _streamCts?.Dispose();
        _streamCts = null;
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static Proto.EnvironmentEnvelope CreateAttachRequest(
        WorldId worldId,
        ConnectionId connectionId,
        CoreSourceSequence sequence,
        SimTick tick,
        string adapterVersion,
        string ss14Revision) => new()
    {
        WorldId = new Proto.WorldId { Value = worldId.ToGuid().ToString("D") },
        ConnectionId = new Proto.ConnectionId { Value = connectionId.ToGuid().ToString("D") },
        Tick = new Proto.SimTick { Value = tick.Value },
        SourceSequence = new Proto.SourceSequence { Value = sequence.Value },
        LatestAck = new Proto.RuntimeSequence { Value = 0 },
        AttachWorldRequest = new Proto.AttachWorldRequest
        {
            WorldId = new Proto.WorldId { Value = worldId.ToGuid().ToString("D") },
            ProtocolVersion = new Proto.ProtocolVersion { Major = 1, Minor = 1 },
            Capabilities = CreateAdapterCapabilities(),
            AdapterIdentity = new Proto.BuildIdentity
            {
                Component = "cogr-station",
                Version = adapterVersion,
                Commit = ss14Revision,
                BuildTime = string.Empty,
            },
        },
    };

    private static Proto.EnvironmentEnvelope CreateAdapterHandshake(
        WorldId worldId,
        ConnectionId connectionId,
        CoreSourceSequence sequence,
        SimTick tick,
        uint experiencedTicksPerSecond,
        string adapterVersion,
        string ss14Revision) => new()
    {
        WorldId = new Proto.WorldId { Value = worldId.ToGuid().ToString("D") },
        ConnectionId = new Proto.ConnectionId { Value = connectionId.ToGuid().ToString("D") },
        Tick = new Proto.SimTick { Value = tick.Value },
        SourceSequence = new Proto.SourceSequence { Value = sequence.Value },
        LatestAck = new Proto.RuntimeSequence { Value = 0 },
        AdapterHandshake = new Proto.AdapterHandshake
        {
            WorldId = new Proto.WorldId { Value = worldId.ToGuid().ToString("D") },
            ConnectionId = new Proto.ConnectionId { Value = connectionId.ToGuid().ToString("D") },
            Negotiated = CreateAdapterCapabilities(),
            ExperiencedTicksPerSecond = experiencedTicksPerSecond,
        },
    };

    private static Proto.CapabilitySet CreateAdapterCapabilities()
    {
        var capabilities = new Proto.CapabilitySet();
        capabilities.Supported.Add(new Proto.Capability { Id = "cogr.action.lifecycle.v1", MinVersion = 1 });
        capabilities.Supported.Add(new Proto.Capability { Id = "cogr.action.diagnostic-step.v1", MinVersion = 1 });
        return capabilities;
    }

    private static bool MatchesContext(Proto.RuntimeEnvelope envelope, WorldId worldId, ConnectionId connectionId) =>
        Guid.TryParse(envelope.WorldId?.Value, out var responseWorld) &&
        Guid.TryParse(envelope.ConnectionId?.Value, out var responseConnection) &&
        responseWorld == worldId.ToGuid() &&
        responseConnection == connectionId.ToGuid();
}

public sealed class COGRHandshakeResult
{
    private COGRHandshakeResult(
        bool succeeded,
        bool isTransient,
        Guid? worldId,
        Guid? connectionId,
        ulong? currentTick,
        ulong? latestRuntimeSequence,
        string? error,
        string? statusCode)
    {
        Succeeded = succeeded;
        IsTransient = isTransient;
        WorldId = worldId;
        ConnectionId = connectionId;
        CurrentTick = currentTick;
        LatestRuntimeSequence = latestRuntimeSequence;
        Error = error;
        StatusCode = statusCode;
    }

    public bool Succeeded { get; }

    /// <summary>
    /// Indicates a transient failure (runtime not ready, network hiccup) that should
    /// be retried without error-level logging.
    /// </summary>
    public bool IsTransient { get; }

    public Guid? WorldId { get; }
    public Guid? ConnectionId { get; }
    public ulong? CurrentTick { get; }
    public ulong? LatestRuntimeSequence { get; }
    public string? Error { get; }

    /// <summary>
    /// The gRPC status code for transient or failed results, if applicable.
    /// </summary>
    public string? StatusCode { get; }

    public static COGRHandshakeResult Success(
        Guid worldId,
        Guid connectionId,
        ulong currentTick,
        ulong latestRuntimeSequence) =>
        new(true, false, worldId, connectionId, currentTick, latestRuntimeSequence, null, null);

    public static COGRHandshakeResult Failed(string error) =>
        new(false, false, null, null, null, null, error, null);

    /// <summary>
    /// Creates a transient failure result indicating the runtime is not ready but may become
    /// available on retry.
    /// </summary>
    public static COGRHandshakeResult Transient(string error, string statusCode) =>
        new(false, true, null, null, null, null, error, statusCode);
}
