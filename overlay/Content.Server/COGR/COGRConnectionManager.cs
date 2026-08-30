using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using COGR.Contracts.Messages;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using COGR.Transport.Grpc.Mapping;
using Google.Protobuf;
using Content.Server.COGR.Transport;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Timing;
using Proto = COGR.Transport.Grpc.Protocol.V1;
using ContractAgentLifecycleType = COGR.Contracts.Messages.AgentLifecycleType;
using ContractWorldLifecycleType = COGR.Contracts.Messages.WorldLifecycleType;

namespace Content.Server.COGR;

/// <summary>
/// Owns one authoritative COGR world identity and one connection-scoped duplex stream.
/// Runtime messages are dispatched only from <see cref="ProcessPendingMessages"/> on the
/// SS14 main thread.
/// </summary>
public sealed class COGRConnectionManager : IDisposable
{
    private readonly COGRAdapterConfiguration _config;
    private readonly ISawmill _sawmill;
    private readonly Func<SimTick> _currentTick;
    private readonly ConcurrentQueue<EnvironmentMessage> _outboundQueue = new();
    private ChannelReader<EnvironmentMessage>? _bridgeMessages;
    private readonly ConcurrentQueue<Proto.RuntimeEnvelope> _inboundQueue = new();

    private COGRGrpcClient? _grpcClient;
    private COGRConnectionState _state = COGRConnectionState.Disconnected;
    private readonly WorldId _worldId;
    private ConnectionId _connectionId;
    private uint _lastHeartbeatTick;
    private ulong _lastSentSourceSequence;
    private ulong _lastReceivedRuntimeSequence;
    private ulong _heartbeatSequence;
    private bool _disposed;
    private uint _messagesSent;
    private uint _messagesReceived;
    private string? _lastError;
    private int _connectionAttempts;
    private DateTime _nextReconnectTime = DateTime.MinValue;

    public const int ProtocolMajorVersion = 1;
    public const int ProtocolMinorVersion = 1;

    public COGRConnectionManager(
        COGRAdapterConfiguration config,
        ISawmill sawmill)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sawmill = sawmill ?? throw new ArgumentNullException(nameof(sawmill));
        var timing = IoCManager.Resolve<IGameTiming>();
        _currentTick = () => new SimTick((ulong)timing.CurTick.Value);
        _worldId = global::COGR.Core.Identifiers.WorldId.FromGuid(Guid.CreateVersion7());
    }

    public COGRConnectionState State => _state;
    public bool IsConnected => _state == COGRConnectionState.Connected && _grpcClient?.IsConnected == true;
    public Guid ConnectionId => _connectionId.IsAssigned ? _connectionId.ToGuid() : Guid.Empty;
    public Guid WorldId => _worldId.ToGuid();
    public uint LastHeartbeatTick => _lastHeartbeatTick;
    public uint MessagesSent => _messagesSent;
    public uint MessagesReceived => _messagesReceived;
    public string? LastError => _lastError;
    public ulong LastSentSourceSequence => _lastSentSourceSequence;
    public ulong LastReceivedRuntimeSequence => _lastReceivedRuntimeSequence;

    public event Action<ActionProposalMessage>? ActionProposalReceived;
    public event Action<ActionCancellationMessage>? ActionCancellationReceived;
    public event Action<PerceptionRequestMessage>? PerceptionRequestReceived;
    public event Action<ContextualAffordanceQuery>? ContextualAffordanceRequested;
    public event Action<SemanticReplicaResyncRequest>? SemanticReplicaResyncRequested;
    public event Action<Proto.AdministrativeResponse>? AdministrativeResponseReceived;

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(COGRConnectionManager));

        // An explicit connect resumes normal automatic recovery after a manual disconnect.
        _config.AutoConnect = true;

        if (IsConnected)
            return true;
        if (_state == COGRConnectionState.Connecting)
            return false;

        _state = COGRConnectionState.Connecting;
        _connectionAttempts++;
        _connectionId = global::COGR.Core.Identifiers.ConnectionId.FromGuid(Guid.CreateVersion7());
        _lastSentSourceSequence = 0;
        _lastReceivedRuntimeSequence = 0;
        _heartbeatSequence = 0;
        ClearQueue(_inboundQueue);
        ClearQueue(_outboundQueue);

        _sawmill.Info(
            "Connecting to COGR runtime at {0} with world {1}, connection {2} (attempt {3})",
            _config.RuntimeEndpoint,
            _worldId,
            _connectionId,
            _connectionAttempts);

        try
        {
            if (_grpcClient != null)
                await DisposeClientAsync().ConfigureAwait(false);

            _grpcClient = new COGRGrpcClient(_config.RuntimeEndpoint, _sawmill);
            _grpcClient.MessageReceived += OnGrpcMessageReceived;
            _grpcClient.Disconnected += OnGrpcDisconnected;

            var result = await _grpcClient.ConnectAsync(
                _worldId,
                _connectionId,
                NextSourceSequence,
                _currentTick,
                adapterVersion: "F3-replica",
                ss14Revision: "cogr-station",
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                _lastError = result.Error;
                await DisposeClientAsync().ConfigureAwait(false);
                _state = COGRConnectionState.Disconnected;
                ScheduleReconnect();

                if (!result.IsTransient)
                {
                    _sawmill.Warning(
                        "COGR connection attempt {0} failed: {1}",
                        _connectionAttempts,
                        result.Error ?? "Unknown error");
                }

                return false;
            }

            _lastReceivedRuntimeSequence = result.LatestRuntimeSequence ?? 0;
            _state = COGRConnectionState.Connected;
            _connectionAttempts = 0;
            _lastError = null;

            SendWorldLifecycle(WorldLifecycleType.Created, "SS14 adapter connected");
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _sawmill.Error("Failed to connect to COGR runtime: {0}", ex.Message);
            await DisposeClientAsync().ConfigureAwait(false);
            _state = COGRConnectionState.Disconnected;
            ScheduleReconnect();
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // A requested disconnect is sticky until ConnectAsync is explicitly invoked again.
        // This also prevents the adapter Update() auto-connect path from immediately reconnecting.
        _config.AutoConnect = false;

        if (_state == COGRConnectionState.Disconnected)
            return;

        _state = COGRConnectionState.Disconnecting;
        if (_grpcClient != null && _connectionId.IsAssigned)
            await _grpcClient.DisconnectAsync(_connectionId, cancellationToken: cancellationToken).ConfigureAwait(false);

        await DisposeClientAsync().ConfigureAwait(false);
        _state = COGRConnectionState.Disconnected;
    }

    public void SendWorldLifecycle(WorldLifecycleType type, string? details = null)
    {
        if (!_connectionId.IsAssigned)
            return;

        EnqueueEnvironmentMessage(new WorldLifecycleMessage
        {
            WorldId = _worldId,
            ConnectionId = _connectionId,
            Tick = _currentTick(),
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            Type = (ContractWorldLifecycleType)(int)type,
            Details = details,
        });
    }

    public void SendAgentLifecycle(Guid agentId, AgentLifecycleType type, string? details = null)
    {
        if (!_connectionId.IsAssigned || agentId == Guid.Empty)
            return;

        EnqueueEnvironmentMessage(new AgentLifecycleMessage
        {
            WorldId = _worldId,
            ConnectionId = _connectionId,
            Tick = _currentTick(),
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            AgentId = AgentId.FromGuid(agentId),
            Type = (ContractAgentLifecycleType)(int)type,
            Details = details,
        });
    }

    public void AttachBridgeMessages(ChannelReader<EnvironmentMessage> messages)
    {
        _bridgeMessages = messages ?? throw new ArgumentNullException(nameof(messages));
    }

    public void DetachBridgeMessages(ChannelReader<EnvironmentMessage> messages)
    {
        if (ReferenceEquals(_bridgeMessages, messages))
            _bridgeMessages = null;
    }

    public Guid SendAdministrativeCommand(
        string command,
        ReadOnlyMemory<byte> parameters,
        string format = "application/json")
    {
        if (!IsConnected || _grpcClient == null)
            throw new InvalidOperationException("COGR runtime is not connected.");
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Administrative command is required.", nameof(command));

        var correlationId = Guid.CreateVersion7();
        var envelope = new Proto.EnvironmentEnvelope
        {
            WorldId = new Proto.WorldId { Value = _worldId.ToGuid().ToString("D") },
            ConnectionId = new Proto.ConnectionId { Value = _connectionId.ToGuid().ToString("D") },
            Tick = new Proto.SimTick { Value = _currentTick().Value },
            SourceSequence = new Proto.SourceSequence { Value = NextSourceSequence().Value },
            LatestAck = new Proto.RuntimeSequence { Value = _lastReceivedRuntimeSequence },
            CorrelationId = new Proto.CorrelationId { Value = correlationId.ToString("D") },
            AdminInput = new Proto.AdministrativeInput
            {
                Command = command,
                Parameters = ByteString.CopyFrom(parameters.Span),
                Format = format,
            },
        };

        _ = SendEnvelopeAsync(envelope);
        _messagesSent++;
        return correlationId;
    }

    /// <summary>
    /// Queues an environment-domain message for the single connection writer. The transport
    /// assigns the final source sequence and latest acknowledgement immediately before mapping.
    /// </summary>
    public void EnqueueEnvironmentMessage(EnvironmentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _outboundQueue.Enqueue(message);
    }

    /// <summary>
    /// Processes received runtime messages and queues outgoing environment messages. This method
    /// must be called from the SS14 main thread.
    /// </summary>
    public void ProcessPendingMessages()
    {
        if (!IsConnected)
        {
            TryReconnect();
            return;
        }

        while (_inboundQueue.TryDequeue(out var envelope))
        {
            try
            {
                ProcessRuntimeEnvelope(envelope);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _sawmill.Error(
                    "COGR runtime envelope processing failed closed: payload={0}, runtimeSequence={1}, error={2}",
                    envelope.PayloadCase,
                    envelope.RuntimeSequence?.Value ?? 0,
                    ex);
            }
        }

        if (_bridgeMessages != null)
        {
            while (_bridgeMessages.TryRead(out var bridgeMessage))
                _outboundQueue.Enqueue(bridgeMessage);
        }

        while (_outboundQueue.TryDequeue(out var message))
            QueueEnvironmentMessage(message);
    }

    public void UpdateHeartbeat(uint currentTick)
    {
        if (!IsConnected || _grpcClient == null)
            return;

        if (currentTick - _lastHeartbeatTick < _config.HeartbeatIntervalTicks)
            return;

        _lastHeartbeatTick = currentTick;
        _heartbeatSequence++;
        _ = _grpcClient.SendHeartbeatAsync(
            _connectionId,
            new SimTick(currentTick),
            _heartbeatSequence);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        DisconnectAsync().GetAwaiter().GetResult();
        _disposed = true;
    }

    private void QueueEnvironmentMessage(EnvironmentMessage message)
    {
        if (_grpcClient == null || !IsConnected)
            return;

        if (message.WorldId != _worldId || message.ConnectionId != _connectionId)
        {
            _sawmill.Warning(
                "Dropping stale COGR environment message {0}: context does not match the active stream",
                message.GetType().Name);
            return;
        }

        var normalized = message with
        {
            SourceSequence = NextSourceSequence(),
            LatestAck = new RuntimeSequence(_lastReceivedRuntimeSequence),
        };

        Proto.EnvironmentEnvelope envelope;
        try
        {
            envelope = normalized switch
            {
                ActionDispositionMessage action => ActionEnvelopeMapper.ToProto(action),
                ActionProgressMessage action => ActionEnvelopeMapper.ToProto(action),
                ActionInterruptionMessage action => ActionEnvelopeMapper.ToProto(action),
                ActionTerminalResultMessage action => ActionEnvelopeMapper.ToProto(action),
                _ => EnvironmentEnvelopeMapper.ToProto(normalized),
            };
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _sawmill.Error("Failed to map COGR environment message {0}: {1}", normalized.GetType().Name, ex.Message);
            return;
        }

        _ = SendEnvelopeAsync(envelope);
        _messagesSent++;
    }

    private async Task SendEnvelopeAsync(Proto.EnvironmentEnvelope envelope)
    {
        try
        {
            if (_grpcClient != null)
                await _grpcClient.SendAsync(envelope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            OnGrpcDisconnected(ex);
        }
    }

    private void ProcessRuntimeEnvelope(Proto.RuntimeEnvelope envelope)
    {
        if (!ValidateRuntimeEnvelope(envelope))
            return;

        _lastReceivedRuntimeSequence = envelope.RuntimeSequence.Value;
        _messagesReceived++;

        switch (envelope.PayloadCase)
        {
            case Proto.RuntimeEnvelope.PayloadOneofCase.ActionProposal:
            {
                var mapped = ActionEnvelopeMapper.ToDomain(envelope);
                if (mapped.IsSuccess && mapped.Value is ActionProposalMessage proposal)
                    ActionProposalReceived?.Invoke(proposal);
                else
                    _sawmill.Warning("Rejected malformed runtime action proposal: {0}", mapped.Error?.ToString() ?? "unknown mapping error");
                break;
            }
            case Proto.RuntimeEnvelope.PayloadOneofCase.ActionCancellation:
            {
                var mapped = ActionEnvelopeMapper.ToDomain(envelope);
                if (mapped.IsSuccess && mapped.Value is ActionCancellationMessage cancellation)
                    ActionCancellationReceived?.Invoke(cancellation);
                else
                    _sawmill.Warning("Rejected malformed runtime action cancellation: {0}", mapped.Error?.ToString() ?? "unknown mapping error");
                break;
            }
            case Proto.RuntimeEnvelope.PayloadOneofCase.PerceptionRequest:
            {
                var mapped = BoundedPerceptionRequestEnvelopeMapper.ToDomain(envelope);
                if (mapped.IsSuccess && mapped.Value is PerceptionRequestMessage request)
                    PerceptionRequestReceived?.Invoke(request);
                else
                    _sawmill.Warning("Rejected malformed runtime perception request: {0}", mapped.Error?.ToString() ?? "unknown mapping error");
                break;
            }
            case Proto.RuntimeEnvelope.PayloadOneofCase.PerceptionExpansion:
            {
                if (ContextualAffordanceWireCodec.IsQueryScope(envelope.PerceptionExpansion.Scope))
                {
                    if (!ContextualAffordanceWireCodec.TryDecodeQueryScope(
                            envelope.PerceptionExpansion.Scope,
                            out var affordanceQuery,
                            out var affordanceError) ||
                        affordanceQuery == null)
                    {
                        _sawmill.Warning(
                            "Rejected malformed contextual affordance query: {0}",
                            affordanceError ?? "unknown query error");
                        break;
                    }

                    if (affordanceQuery.ConnectionId != _connectionId ||
                        affordanceQuery.AgentId.ToString() != envelope.PerceptionExpansion.AgentId?.Value ||
                        !Guid.TryParse(envelope.CorrelationId?.Value, out var correlationId) ||
                        correlationId != affordanceQuery.QueryId)
                    {
                        _sawmill.Warning("Rejected contextual affordance query with mismatched authority or correlation scope");
                        break;
                    }

                    ContextualAffordanceRequested?.Invoke(affordanceQuery);
                    break;
                }

                if (!SemanticReplicaWireCodec.TryDecodeResyncScope(
                        envelope.PerceptionExpansion.Scope,
                        out var request,
                        out var error) ||
                    request == null)
                {
                    _sawmill.Debug(
                        "Ignoring unsupported perception expansion request: {0}",
                        error ?? "unknown scope");
                    break;
                }

                if (request.Scope.ConnectionId != _connectionId ||
                    request.Scope.AgentId.ToString() != envelope.PerceptionExpansion.AgentId?.Value)
                {
                    _sawmill.Warning("Rejected semantic replica resync request with mismatched authority scope");
                    break;
                }

                SemanticReplicaResyncRequested?.Invoke(request);
                break;
            }
            case Proto.RuntimeEnvelope.PayloadOneofCase.AdminResponse:
                AdministrativeResponseReceived?.Invoke(envelope.AdminResponse);
                break;
            default:
                _sawmill.Debug("COGR RX runtime payload {0}", envelope.PayloadCase);
                break;
        }
    }

    private bool ValidateRuntimeEnvelope(Proto.RuntimeEnvelope envelope)
    {
        if (!Guid.TryParse(envelope.WorldId?.Value, out var worldId) ||
            !Guid.TryParse(envelope.ConnectionId?.Value, out var connectionId) ||
            worldId != _worldId.ToGuid() ||
            connectionId != _connectionId.ToGuid())
        {
            _sawmill.Warning("Dropping runtime envelope from stale or invalid context");
            return false;
        }

        var sequence = envelope.RuntimeSequence?.Value ?? 0;
        if (sequence == 0 || sequence <= _lastReceivedRuntimeSequence)
        {
            _sawmill.Warning(
                "Dropping runtime envelope with non-advancing sequence {0}; latest is {1}",
                sequence,
                _lastReceivedRuntimeSequence);
            return false;
        }

        return true;
    }

    private SourceSequence NextSourceSequence() =>
        new(Interlocked.Increment(ref _lastSentSourceSequence));

    private void OnGrpcMessageReceived(Proto.RuntimeEnvelope envelope) =>
        _inboundQueue.Enqueue(envelope);

    private void OnGrpcDisconnected(Exception? exception)
    {
        if (_state is COGRConnectionState.Disconnecting or COGRConnectionState.Disconnected)
            return;

        _lastError = exception?.Message ?? "Runtime closed the duplex stream.";
        _state = COGRConnectionState.Disconnected;
        ScheduleReconnect();
    }

    private void TryReconnect()
    {
        if (!_config.AutoConnect ||
            _state != COGRConnectionState.Disconnected ||
            DateTime.UtcNow < _nextReconnectTime)
            return;
        _ = ConnectAsync();
    }

    private void ScheduleReconnect()
    {
        var attempt = Math.Max(_connectionAttempts, 1);
        var delay = TimeSpan.FromMilliseconds(_config.ReconnectDelayMs * Math.Min(attempt, 10));
        _nextReconnectTime = DateTime.UtcNow + delay;
    }

    private async Task DisposeClientAsync()
    {
        if (_grpcClient == null)
            return;
        _grpcClient.MessageReceived -= OnGrpcMessageReceived;
        _grpcClient.Disconnected -= OnGrpcDisconnected;
        await _grpcClient.DisposeAsync().ConfigureAwait(false);
        _grpcClient = null;
    }

    private static void ClearQueue<T>(ConcurrentQueue<T> queue)
    {
        while (queue.TryDequeue(out _))
        {
        }
    }
}

public enum COGRConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Error,
}
