using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using COGR.Core.Identifiers;
using Content.Server.Administration.Managers;
using Content.Shared.Administration;
using Content.Shared.COGR.SpatialVisualization;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Proto = COGR.Transport.Grpc.Protocol.V1;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Admin-only realization of one explicitly selected Coggent's spatial diagnostics. Runtime reports cognition-owned local
/// vectors; Station converts those vectors into map coordinates solely for visualization. Station truth never repairs,
/// replaces, or feeds back into the reported COGR belief.
/// </summary>
public sealed partial class COGRSpatialVisualizationSystem : EntitySystem
{
    private const ulong PollIntervalTicks = 5;

    [Dependency] private IAdminManager _admin = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly Dictionary<ICommonSession, string> _subscriberAgents = [];
    private readonly Dictionary<string, ulong> _latestPathSequenceByAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _latestNavigationTraceSequenceByAgent = new(StringComparer.OrdinalIgnoreCase);

    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private ISawmill _traceSawmill = default!;
    private COGRConnectionManager? _subscribedConnection;
    private Guid? _pendingPollCorrelation;
    private string? _pendingPollAgentId;
    private ulong _lastPollTick;
    private int _pollCursor;

    public override void Initialize()
    {
        base.Initialize();
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _traceSawmill = _logManager.GetSawmill("cogr.navtrace");
        SubscribeNetworkEvent<RequestCOGRSpatialVisualizationMessage>(OnSubscriptionRequest);
    }

    public override void Shutdown()
    {
        DisableAllRuntimeObservers();
        AttachConnection(null);
        _subscriberAgents.Clear();
        ClearDiagnosticState();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        AttachConnection(_adapter.Connection);

        if (_subscriberAgents.Count == 0
            || _subscribedConnection is not { IsConnected: true }
            || _pendingPollCorrelation.HasValue)
        {
            return;
        }

        var currentTick = (ulong)_timing.CurTick.Value;
        if (currentTick - _lastPollTick < PollIntervalTicks)
            return;

        var agents = _subscriberAgents.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (agents.Length == 0)
            return;

        if (_pollCursor >= agents.Length)
            _pollCursor = 0;
        var agentId = agents[_pollCursor];
        _pollCursor = (_pollCursor + 1) % agents.Length;
        _lastPollTick = currentTick;
        SendPoll(agentId, enabled: true, trackResponse: true);
    }

    private void OnSubscriptionRequest(
        RequestCOGRSpatialVisualizationMessage message,
        EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_admin.HasAdminFlag(session, AdminFlags.Debug))
        {
            RemoveSubscriber(session);
            return;
        }

        if (!Guid.TryParse(message.AgentId, out var agentGuid) || agentGuid == Guid.Empty)
            return;
        var agentId = agentGuid.ToString("D");

        if (message.Enabled)
        {
            _subscriberAgents.TryGetValue(session, out var previousAgentId);
            _subscriberAgents[session] = agentId;
            _lastPollTick = 0;
            if (previousAgentId is not null
                && !string.Equals(previousAgentId, agentId, StringComparison.OrdinalIgnoreCase))
            {
                DisableIfUnobserved(previousAgentId);
            }
            return;
        }

        if (_subscriberAgents.TryGetValue(session, out var selected)
            && string.Equals(selected, agentId, StringComparison.OrdinalIgnoreCase))
        {
            _subscriberAgents.Remove(session);
            DisableIfUnobserved(selected);
        }
    }

    private void RemoveSubscriber(ICommonSession session)
    {
        if (!_subscriberAgents.Remove(session, out var agentId))
            return;
        DisableIfUnobserved(agentId);
    }

    private void DisableIfUnobserved(string agentId)
    {
        if (_subscriberAgents.Values.Any(selected => string.Equals(selected, agentId, StringComparison.OrdinalIgnoreCase)))
            return;

        SendPoll(agentId, enabled: false, trackResponse: false);
        _latestPathSequenceByAgent.Remove(agentId);
        _latestNavigationTraceSequenceByAgent.Remove(agentId);
        if (string.Equals(_pendingPollAgentId, agentId, StringComparison.OrdinalIgnoreCase))
        {
            _pendingPollCorrelation = null;
            _pendingPollAgentId = null;
        }
    }

    private void AttachConnection(COGRConnectionManager? connection)
    {
        if (ReferenceEquals(connection, _subscribedConnection))
            return;

        if (_subscribedConnection is not null)
            _subscribedConnection.AdministrativeResponseReceived -= OnAdministrativeResponse;

        _subscribedConnection = connection;
        _pendingPollCorrelation = null;
        _pendingPollAgentId = null;
        ClearDiagnosticState();

        if (_subscribedConnection is not null)
            _subscribedConnection.AdministrativeResponseReceived += OnAdministrativeResponse;
    }

    private void SendPoll(string agentId, bool enabled, bool trackResponse)
    {
        var connection = _subscribedConnection;
        if (connection is not { IsConnected: true })
            return;

        var parameters = JsonSerializer.SerializeToUtf8Bytes(new
        {
            enabled,
            agentId,
            afterPathSequence = enabled ? _latestPathSequenceByAgent.GetValueOrDefault(agentId) : 0UL,
            afterNavigationTraceSequence = enabled
                ? _latestNavigationTraceSequenceByAgent.GetValueOrDefault(agentId)
                : 0UL,
        });

        try
        {
            var correlation = connection.SendAdministrativeCommand(
                "cogr.debug.spatial.poll",
                parameters);
            if (trackResponse)
            {
                _pendingPollCorrelation = correlation;
                _pendingPollAgentId = agentId;
            }
        }
        catch (InvalidOperationException)
        {
            if (trackResponse)
            {
                _pendingPollCorrelation = null;
                _pendingPollAgentId = null;
            }
        }
    }

    private void DisableAllRuntimeObservers()
    {
        foreach (var agentId in _subscriberAgents.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            SendPoll(agentId, enabled: false, trackResponse: false);
        _pendingPollCorrelation = null;
        _pendingPollAgentId = null;
    }

    private void OnAdministrativeResponse(Proto.AdministrativeResponse response)
    {
        if (!_pendingPollCorrelation.HasValue
            || _pendingPollAgentId is null
            || !Guid.TryParse(response.CorrelationId?.Value, out var correlation)
            || correlation != _pendingPollCorrelation.Value)
        {
            return;
        }

        var requestedAgentId = _pendingPollAgentId;
        _pendingPollCorrelation = null;
        _pendingPollAgentId = null;
        if (!response.Success)
            return;

        SpatialPollPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SpatialPollPayload>(
                Encoding.UTF8.GetString(response.Data.Span),
                SpatialJsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (payload is null
            || !payload.Enabled
            || !string.Equals(payload.AgentId, requestedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _latestPathSequenceByAgent[requestedAgentId] = Math.Max(
            _latestPathSequenceByAgent.GetValueOrDefault(requestedAgentId),
            payload.LatestPathSequence);
        _latestNavigationTraceSequenceByAgent[requestedAgentId] = Math.Max(
            _latestNavigationTraceSequenceByAgent.GetValueOrDefault(requestedAgentId),
            payload.LatestNavigationTraceSequence);

        foreach (var trace in payload.NavigationTrace.OrderBy(static entry => entry.Sequence))
        {
            if (!string.Equals(trace.AgentId, requestedAgentId, StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = string.IsNullOrWhiteSpace(trace.Detail) ? string.Empty : $" ({trace.Detail})";
            _traceSawmill.Info("{0} -> {1}{2}", trace.Stage, trace.Outcome, suffix);
        }

        var message = ResolvePayload(payload);
        foreach (var subscriber in _subscriberAgents
                     .Where(pair => string.Equals(pair.Value, requestedAgentId, StringComparison.OrdinalIgnoreCase))
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            if (!_admin.HasAdminFlag(subscriber, AdminFlags.Debug))
            {
                RemoveSubscriber(subscriber);
                continue;
            }

            // Send every successful poll, including an empty target set. The empty full frame is the authoritative debug
            // deletion signal for beliefs that COGR no longer reports.
            RaiseNetworkEvent(message, subscriber.Channel);
        }
    }

    private COGRSpatialVisualizationMessage ResolvePayload(SpatialPollPayload payload)
    {
        var empty = new COGRSpatialVisualizationMessage { AgentId = payload.AgentId };
        if (_subscribedConnection is not { IsConnected: true } connection
            || connection.ConnectionId == Guid.Empty)
        {
            return empty;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        var targets = new List<COGRSpatialVisualizationTarget>();
        foreach (var target in payload.Targets)
        {
            if (!string.Equals(target.AgentId, payload.AgentId, StringComparison.OrdinalIgnoreCase)
                || !TryResolveBodyFrame(
                    connectionId,
                    target.AgentId,
                    out _,
                    out var bodyCoordinates,
                    out var worldRotation)
                || !TryRealizeLocalPoint(
                    bodyCoordinates,
                    worldRotation,
                    target.LocalX,
                    target.LocalY,
                    target.LocalZ,
                    out var beliefCoordinates))
            {
                continue;
            }

            targets.Add(new COGRSpatialVisualizationTarget
            {
                AgentId = target.AgentId,
                TargetId = target.TargetId,
                TargetRevision = target.TargetRevision,
                IsTracked = target.IsTracked,
                Belief = beliefCoordinates,
            });
        }

        var paths = new List<COGRSpatialVisualizationPath>();
        foreach (var path in payload.Paths)
        {
            if (!string.Equals(path.AgentId, payload.AgentId, StringComparison.OrdinalIgnoreCase)
                || !TryResolveBodyFrame(
                    connectionId,
                    path.AgentId,
                    out _,
                    out var bodyCoordinates,
                    out var worldRotation))
            {
                continue;
            }

            var points = new List<MapCoordinates>(path.Points.Length);
            foreach (var point in path.Points)
            {
                if (!TryRealizeLocalPoint(
                        bodyCoordinates,
                        worldRotation,
                        point.X,
                        point.Y,
                        point.Z,
                        out var realized))
                {
                    points.Clear();
                    break;
                }

                points.Add(realized);
            }

            if (points.Count < 2)
                continue;

            paths.Add(new COGRSpatialVisualizationPath
            {
                Sequence = path.Sequence,
                Points = points.ToArray(),
            });
        }

        return new COGRSpatialVisualizationMessage
        {
            AgentId = payload.AgentId,
            Targets = targets.ToArray(),
            Paths = paths.ToArray(),
        };
    }

    private bool TryResolveBodyFrame(
        ConnectionId connectionId,
        string rawAgentId,
        out AgentId agentId,
        out MapCoordinates bodyCoordinates,
        out Angle worldRotation)
    {
        agentId = default;
        bodyCoordinates = default;
        worldRotation = default;

        if (!Guid.TryParse(rawAgentId, out var agentGuid) || agentGuid == Guid.Empty)
            return false;
        agentId = AgentId.FromGuid(agentGuid);

        var lease = _authority.ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue)
            return false;

        var resolvedBody = _authority.ResolveBoundBody(
            agentId,
            lease.Value.BodyId,
            connectionId,
            lease.Value.Generation);
        if (!resolvedBody.HasValue || !TryComp(resolvedBody.Value, out TransformComponent? xform))
            return false;

        bodyCoordinates = _transform.GetMapCoordinates(resolvedBody.Value, xform: xform);
        if (bodyCoordinates.MapId == MapId.Nullspace)
            return false;
        worldRotation = _transform.GetWorldRotation(xform);
        return true;
    }

    private static bool TryRealizeLocalPoint(
        MapCoordinates origin,
        Angle worldRotation,
        double localX,
        double localY,
        double localZ,
        out MapCoordinates realized)
    {
        realized = default;
        if (!double.IsFinite(localX) || !double.IsFinite(localY) || !double.IsFinite(localZ))
            return false;

        var nativeX = COGREmbodimentSpatialCalibration.LocalUnitsToNativeUnits(
            COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
            localX);
        var nativeY = COGREmbodimentSpatialCalibration.LocalUnitsToNativeUnits(
            COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
            localY);
        if (!double.IsFinite(nativeX) || !double.IsFinite(nativeY))
            return false;

        var cos = Math.Cos(worldRotation.Theta);
        var sin = Math.Sin(worldRotation.Theta);
        var offset = new Vector2(
            (float)((nativeX * cos) - (nativeY * sin)),
            (float)((nativeX * sin) + (nativeY * cos)));
        realized = new MapCoordinates(origin.Position + offset, origin.MapId);
        return true;
    }

    private void ClearDiagnosticState()
    {
        _latestPathSequenceByAgent.Clear();
        _latestNavigationTraceSequenceByAgent.Clear();
        _pollCursor = 0;
    }

    private static readonly JsonSerializerOptions SpatialJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class SpatialPollPayload
    {
        public bool Enabled { get; init; }
        public string AgentId { get; init; } = string.Empty;
        public ulong LatestPathSequence { get; init; }
        public ulong LatestNavigationTraceSequence { get; init; }
        public SpatialTargetPayload[] Targets { get; init; } = [];
        public SpatialPathPayload[] Paths { get; init; } = [];
        public NavigationTracePayload[] NavigationTrace { get; init; } = [];
    }

    private sealed class SpatialTargetPayload
    {
        public string AgentId { get; init; } = string.Empty;
        public string TargetId { get; init; } = string.Empty;
        public ulong TargetRevision { get; init; }
        public bool IsTracked { get; init; }
        public double LocalX { get; init; }
        public double LocalY { get; init; }
        public double LocalZ { get; init; }
    }

    private sealed class SpatialPathPayload
    {
        public ulong Sequence { get; init; }
        public string AgentId { get; init; } = string.Empty;
        public SpatialPointPayload[] Points { get; init; } = [];
    }

    private sealed class SpatialPointPayload
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Z { get; init; }
    }

    private sealed class NavigationTracePayload
    {
        public ulong Sequence { get; init; }
        public string AgentId { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public string Outcome { get; init; } = string.Empty;
        public string? Detail { get; init; }
    }
}
