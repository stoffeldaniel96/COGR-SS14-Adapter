using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Time;
using Content.Server.Administration.Managers;
using Content.Shared.Administration;
using Content.Shared.COGR.SpatialVisualization;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Proto = COGR.Transport.Grpc.Protocol.V1;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Admin-only realization of COGR spatial diagnostics. Runtime reports cognition-owned local vectors and optional opaque
/// referents; Station resolves those into authoritative map coordinates solely for visualization. Nothing produced here is
/// returned to cognition, admitted as perception, or used as action/path authority.
/// </summary>
public sealed partial class COGRSpatialVisualizationSystem : EntitySystem
{
    private const ulong PollIntervalTicks = 5;
    private const float PositionChangeEpsilonSquared = 0.0001f;

    [Dependency] private IAdminManager _admin = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly HashSet<ICommonSession> _subscribers = [];
    private readonly Dictionary<string, MapCoordinates> _lastBeliefPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TargetComparisonState> _lastComparisonStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityUid> _actualEntities = new(StringComparer.Ordinal);

    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private COGRConnectionManager? _subscribedConnection;
    private Guid? _pendingPollCorrelation;
    private ulong _lastPollTick;
    private ulong _latestPathSequence;

    public override void Initialize()
    {
        base.Initialize();
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        SubscribeNetworkEvent<RequestCOGRSpatialVisualizationMessage>(OnSubscriptionRequest);
    }

    public override void Shutdown()
    {
        DisableRuntimeObserver();
        AttachConnection(null);
        _subscribers.Clear();
        ClearResolvedState();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        AttachConnection(_adapter.Connection);

        if (_subscribers.Count == 0
            || _subscribedConnection is not { IsConnected: true }
            || _pendingPollCorrelation.HasValue)
        {
            return;
        }

        var currentTick = (ulong)_timing.CurTick.Value;
        if (currentTick - _lastPollTick < PollIntervalTicks)
            return;

        _lastPollTick = currentTick;
        SendPoll(enabled: true);
    }

    private void OnSubscriptionRequest(
        RequestCOGRSpatialVisualizationMessage message,
        EntitySessionEventArgs args)
    {
        if (!_admin.HasAdminFlag(args.SenderSession, AdminFlags.Debug))
        {
            _subscribers.Remove(args.SenderSession);
            return;
        }

        if (message.Enabled)
        {
            _subscribers.Add(args.SenderSession);
            _lastPollTick = 0;
            return;
        }

        _subscribers.Remove(args.SenderSession);
        if (_subscribers.Count != 0)
            return;

        DisableRuntimeObserver();
        ClearResolvedState();
    }

    private void AttachConnection(COGRConnectionManager? connection)
    {
        if (ReferenceEquals(connection, _subscribedConnection))
            return;

        if (_subscribedConnection is not null)
            _subscribedConnection.AdministrativeResponseReceived -= OnAdministrativeResponse;

        _subscribedConnection = connection;
        _pendingPollCorrelation = null;
        _latestPathSequence = 0;
        ClearResolvedState();

        if (_subscribedConnection is not null)
            _subscribedConnection.AdministrativeResponseReceived += OnAdministrativeResponse;
    }

    private void SendPoll(bool enabled)
    {
        var connection = _subscribedConnection;
        if (connection is not { IsConnected: true })
            return;

        var parameters = JsonSerializer.SerializeToUtf8Bytes(new
        {
            enabled,
            afterPathSequence = enabled ? _latestPathSequence : 0UL,
        });

        try
        {
            var correlation = connection.SendAdministrativeCommand(
                "cogr.debug.spatial.poll",
                parameters);
            if (enabled)
                _pendingPollCorrelation = correlation;
        }
        catch (InvalidOperationException)
        {
            _pendingPollCorrelation = null;
        }
    }

    private void DisableRuntimeObserver()
    {
        if (_subscribedConnection is { IsConnected: true })
            SendPoll(enabled: false);
        _pendingPollCorrelation = null;
    }

    private void OnAdministrativeResponse(Proto.AdministrativeResponse response)
    {
        if (!_pendingPollCorrelation.HasValue
            || !Guid.TryParse(response.CorrelationId?.Value, out var correlation)
            || correlation != _pendingPollCorrelation.Value)
        {
            return;
        }

        _pendingPollCorrelation = null;
        if (!response.Success || _subscribers.Count == 0)
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

        if (payload is null || !payload.Enabled)
            return;

        _latestPathSequence = Math.Max(_latestPathSequence, payload.LatestPathSequence);
        var message = ResolvePayload(payload);
        if (message.Targets.Length == 0 && message.Paths.Length == 0)
            return;

        foreach (var subscriber in _subscribers.ToArray())
        {
            if (!_admin.HasAdminFlag(subscriber, AdminFlags.Debug))
            {
                _subscribers.Remove(subscriber);
                continue;
            }

            RaiseNetworkEvent(message, subscriber.Channel);
        }

        if (_subscribers.Count == 0)
        {
            DisableRuntimeObserver();
            ClearResolvedState();
        }
    }

    private COGRSpatialVisualizationMessage ResolvePayload(SpatialPollPayload payload)
    {
        if (_subscribedConnection is not { IsConnected: true } connection
            || connection.ConnectionId == Guid.Empty)
        {
            return new COGRSpatialVisualizationMessage();
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        var currentTick = new SimTick((ulong)_timing.CurTick.Value);
        var targets = new List<COGRSpatialVisualizationTarget>();
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in payload.Targets)
        {
            if (!TryResolveBodyFrame(
                    connectionId,
                    target.AgentId,
                    out var agentId,
                    out var lease,
                    out var bodyCoordinates,
                    out var worldRotation))
            {
                continue;
            }

            if (!TryRealizeLocalPoint(
                    bodyCoordinates,
                    worldRotation,
                    target.LocalX,
                    target.LocalY,
                    target.LocalZ,
                    out var beliefCoordinates))
            {
                continue;
            }

            var key = string.Concat(target.AgentId, ":", target.TargetId);
            currentKeys.Add(key);
            var pulsePointer = !_lastBeliefPositions.TryGetValue(key, out var previousBelief)
                               || CoordinatesDiffer(previousBelief, beliefCoordinates);
            _lastBeliefPositions[key] = beliefCoordinates;

            var actual = default(MapCoordinates);
            var hasActual = TryResolveActualTarget(
                key,
                target.ActualEnvironmentReference,
                agentId,
                lease.BodyId,
                lease.Generation,
                connectionId,
                currentTick,
                bodyCoordinates.MapId,
                out actual);

            var comparisonState = new TargetComparisonState(
                target.IsTracked,
                bodyCoordinates,
                beliefCoordinates,
                hasActual,
                actual);
            var emitComparison = target.IsTracked
                                 && (!_lastComparisonStates.TryGetValue(key, out var previousComparison)
                                     || !previousComparison.IsTracked
                                     || CoordinatesDiffer(previousComparison.Body, bodyCoordinates)
                                     || CoordinatesDiffer(previousComparison.Belief, beliefCoordinates)
                                     || previousComparison.HasActual != hasActual
                                     || (hasActual && CoordinatesDiffer(previousComparison.Actual, actual)));
            _lastComparisonStates[key] = comparisonState;

            if (!pulsePointer && !emitComparison)
                continue;

            targets.Add(new COGRSpatialVisualizationTarget
            {
                AgentId = target.AgentId,
                TargetId = target.TargetId,
                TargetRevision = target.TargetRevision,
                IsTracked = emitComparison,
                Body = bodyCoordinates,
                Belief = beliefCoordinates,
                HasActual = hasActual,
                Actual = actual,
                PulsePointer = pulsePointer,
            });
        }

        foreach (var key in _lastBeliefPositions.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            _lastBeliefPositions.Remove(key);
            _lastComparisonStates.Remove(key);
            _actualEntities.Remove(key);
        }

        var paths = new List<COGRSpatialVisualizationPath>();
        foreach (var path in payload.Paths)
        {
            if (!TryResolveBodyFrame(
                    connectionId,
                    path.AgentId,
                    out _,
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
            Targets = targets.ToArray(),
            Paths = paths.ToArray(),
        };
    }

    private bool TryResolveBodyFrame(
        ConnectionId connectionId,
        string rawAgentId,
        out AgentId agentId,
        out global::COGR.Core.Actions.BodyAuthorityLease lease,
        out MapCoordinates bodyCoordinates,
        out Angle worldRotation)
    {
        agentId = default;
        lease = default;
        bodyCoordinates = default;
        worldRotation = default;

        if (!Guid.TryParse(rawAgentId, out var agentGuid) || agentGuid == Guid.Empty)
            return false;
        agentId = AgentId.FromGuid(agentGuid);

        var resolvedLease = _authority.ResolveBoundLease(agentId, connectionId);
        if (!resolvedLease.HasValue)
            return false;
        lease = resolvedLease.Value;

        var resolvedBody = _authority.ResolveBoundBody(
            agentId,
            lease.BodyId,
            connectionId,
            lease.Generation);
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

    private bool TryResolveActualTarget(
        string key,
        string? rawEnvironmentReference,
        AgentId agentId,
        BodyId bodyId,
        uint bodyGeneration,
        ConnectionId connectionId,
        SimTick currentTick,
        MapId requiredMap,
        out MapCoordinates actual)
    {
        actual = default;
        var registry = _adapter.ReferenceRegistry;
        if (registry is not null
            && Guid.TryParse(rawEnvironmentReference, out var referenceGuid)
            && referenceGuid != Guid.Empty)
        {
            var resolved = registry.TryResolve(
                EnvironmentRef.FromGuid(referenceGuid),
                new EnvironmentReferenceResolutionContext
                {
                    ConnectionId = connectionId,
                    CurrentTick = currentTick,
                    BodyId = bodyId,
                    BodyGeneration = bodyGeneration,
                });
            if (resolved.HasValue)
                _actualEntities[key] = resolved.Value;
        }

        if (!_actualEntities.TryGetValue(key, out var entity)
            || !Exists(entity)
            || !TryComp(entity, out TransformComponent? xform))
        {
            _actualEntities.Remove(key);
            return false;
        }

        actual = _transform.GetMapCoordinates(entity, xform: xform);
        return actual.MapId == requiredMap;
    }

    private static bool CoordinatesDiffer(MapCoordinates left, MapCoordinates right) =>
        left.MapId != right.MapId
        || Vector2.DistanceSquared(left.Position, right.Position) > PositionChangeEpsilonSquared;

    private void ClearResolvedState()
    {
        _lastBeliefPositions.Clear();
        _lastComparisonStates.Clear();
        _actualEntities.Clear();
        _latestPathSequence = 0;
    }

    private static readonly JsonSerializerOptions SpatialJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly record struct TargetComparisonState(
        bool IsTracked,
        MapCoordinates Body,
        MapCoordinates Belief,
        bool HasActual,
        MapCoordinates Actual);

    private sealed class SpatialPollPayload
    {
        public bool Enabled { get; init; }
        public ulong LatestPathSequence { get; init; }
        public SpatialTargetPayload[] Targets { get; init; } = [];
        public SpatialPathPayload[] Paths { get; init; } = [];
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
        public string? ActualEnvironmentReference { get; init; }
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
}
