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
    private readonly Dictionary<ICommonSession, string> _subscriberTargetIds = [];
    private readonly Dictionary<string, ulong> _latestPathSequenceByAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _latestNavigationTraceSequenceByAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _latestFocusTraceSequenceByAgent = new(StringComparer.OrdinalIgnoreCase);

    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private COGRBodyMotionSensationSystem _bodyMotion = default!;
    private ISawmill _traceSawmill = default!;
    private ISawmill _focusSawmill = default!;
    private ISawmill _spatialTraceSawmill = default!;
    private COGRConnectionManager? _subscribedConnection;
    private Guid? _pendingPollCorrelation;
    private string? _pendingPollAgentId;
    private SpatialPollBodyFrame? _pendingPollBodyFrame;
    private ulong _lastPollTick;
    private int _pollCursor;

    public override void Initialize()
    {
        base.Initialize();
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _bodyMotion = EntityManager.System<COGRBodyMotionSensationSystem>();
        _traceSawmill = _logManager.GetSawmill("cogr.navtrace");
        _focusSawmill = _logManager.GetSawmill("cogr.focus");
        _spatialTraceSawmill = _logManager.GetSawmill("cogr.spatialtrace");
        SubscribeNetworkEvent<RequestCOGRSpatialVisualizationMessage>(OnSubscriptionRequest);
    }

    public override void Shutdown()
    {
        DisableAllRuntimeObservers();
        AttachConnection(null);
        _subscriberAgents.Clear();
        _subscriberTargetIds.Clear();
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
            _subscriberTargetIds[session] = string.IsNullOrWhiteSpace(message.TargetId)
                ? string.Empty
                : message.TargetId.Trim();
            COGRSpatialCalibrationDiagnosticCache.SetEnabled(AgentId.FromGuid(agentGuid), true);
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
            _subscriberTargetIds.Remove(session);
            DisableIfUnobserved(selected);
        }
    }

    private void RemoveSubscriber(ICommonSession session)
    {
        if (!_subscriberAgents.Remove(session, out var agentId))
        {
            _subscriberTargetIds.Remove(session);
            return;
        }

        _subscriberTargetIds.Remove(session);
        DisableIfUnobserved(agentId);
    }

    private void DisableIfUnobserved(string agentId)
    {
        if (_subscriberAgents.Values.Any(selected => string.Equals(selected, agentId, StringComparison.OrdinalIgnoreCase)))
            return;

        SendPoll(agentId, enabled: false, trackResponse: false);
        _latestPathSequenceByAgent.Remove(agentId);
        _latestNavigationTraceSequenceByAgent.Remove(agentId);
        _latestFocusTraceSequenceByAgent.Remove(agentId);
        if (Guid.TryParse(agentId, out var agentGuid) && agentGuid != Guid.Empty)
            COGRSpatialCalibrationDiagnosticCache.SetEnabled(AgentId.FromGuid(agentGuid), false);
        if (string.Equals(_pendingPollAgentId, agentId, StringComparison.OrdinalIgnoreCase))
        {
            _pendingPollCorrelation = null;
            _pendingPollAgentId = null;
            _pendingPollBodyFrame = null;
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
        _pendingPollBodyFrame = null;
        ClearDiagnosticState();

        if (_subscribedConnection is not null)
            _subscribedConnection.AdministrativeResponseReceived += OnAdministrativeResponse;
    }

    private void SendPoll(string agentId, bool enabled, bool trackResponse)
    {
        if (Guid.TryParse(agentId, out var diagnosticAgentGuid) && diagnosticAgentGuid != Guid.Empty)
            COGRSpatialCalibrationDiagnosticCache.SetEnabled(AgentId.FromGuid(diagnosticAgentGuid), enabled);

        var connection = _subscribedConnection;
        if (connection is not { IsConnected: true })
            return;

        SpatialPollBodyFrame? bodyFrame = null;
        if (trackResponse)
        {
            if (!TryCaptureCausalPollBodyFrame(connection, agentId, out var captured))
            {
                _pendingPollCorrelation = null;
                _pendingPollAgentId = null;
                _pendingPollBodyFrame = null;
                return;
            }

            bodyFrame = captured;
        }

        var parameters = JsonSerializer.SerializeToUtf8Bytes(new
        {
            enabled,
            agentId,
            afterPathSequence = enabled ? _latestPathSequenceByAgent.GetValueOrDefault(agentId) : 0UL,
            afterNavigationTraceSequence = enabled
                ? _latestNavigationTraceSequenceByAgent.GetValueOrDefault(agentId)
                : 0UL,
            afterFocusTraceSequence = enabled
                ? _latestFocusTraceSequenceByAgent.GetValueOrDefault(agentId)
                : 0UL,
        });

        try
        {
            // SendAdministrativeCommand first drains already-produced environment evidence. For tracked polls the body-motion
            // boundary above therefore enters the same source-sequence stream before this snapshot command, and the captured
            // host frame is the physical frame against which the returned cognition-owned local vector must be visualized.
            var correlation = connection.SendAdministrativeCommand(
                "cogr.debug.spatial.poll",
                parameters);
            if (trackResponse)
            {
                _pendingPollCorrelation = correlation;
                _pendingPollAgentId = agentId;
                _pendingPollBodyFrame = bodyFrame;
            }
        }
        catch (InvalidOperationException)
        {
            if (trackResponse)
            {
                _pendingPollCorrelation = null;
                _pendingPollAgentId = null;
                _pendingPollBodyFrame = null;
            }
        }
    }

    private bool TryCaptureCausalPollBodyFrame(
        COGRConnectionManager connection,
        string rawAgentId,
        out SpatialPollBodyFrame bodyFrame)
    {
        bodyFrame = default;
        if (connection.ConnectionId == Guid.Empty
            || !Guid.TryParse(rawAgentId, out var agentGuid)
            || agentGuid == Guid.Empty)
        {
            return false;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        var agentId = AgentId.FromGuid(agentGuid);
        var lease = _authority.ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue || lease.Value.Generation == 0)
            return false;

        var bodyId = lease.Value.BodyId;
        var resolvedBody = _authority.ResolveBoundBody(
            agentId,
            bodyId,
            connectionId,
            lease.Value.Generation);
        if (!resolvedBody.HasValue)
            return false;

        // Close any pending continuous owner motion before sampling the diagnostic ego frame. The generated proprioceptive
        // evidence remains ordinary environment evidence; SendAdministrativeCommand drains it ahead of the admin poll.
        _bodyMotion.NotifyOwnerFrameSamplingBoundary(resolvedBody.Value);

        if (!TryComp(resolvedBody.Value, out TransformComponent? xform))
            return false;
        var origin = xform.Coordinates;
        if (origin.EntityId == EntityUid.Invalid
            || _transform.ToMapCoordinates(origin).MapId == MapId.Nullspace)
        {
            return false;
        }

        bodyFrame = new SpatialPollBodyFrame(
            agentId,
            bodyId,
            lease.Value.Generation,
            resolvedBody.Value,
            origin,
            xform.LocalRotation,
            new SimTick((ulong)_timing.CurTick.Value));
        return true;
    }

    private void DisableAllRuntimeObservers()
    {
        foreach (var agentId in _subscriberAgents.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            SendPoll(agentId, enabled: false, trackResponse: false);
        _pendingPollCorrelation = null;
        _pendingPollAgentId = null;
        _pendingPollBodyFrame = null;
    }

    private void OnAdministrativeResponse(Proto.AdministrativeResponse response)
    {
        if (!_pendingPollCorrelation.HasValue
            || _pendingPollAgentId is null
            || !_pendingPollBodyFrame.HasValue
            || !Guid.TryParse(response.CorrelationId?.Value, out var correlation)
            || correlation != _pendingPollCorrelation.Value)
        {
            return;
        }

        var requestedAgentId = _pendingPollAgentId;
        var bodyFrame = _pendingPollBodyFrame.Value;
        _pendingPollCorrelation = null;
        _pendingPollAgentId = null;
        _pendingPollBodyFrame = null;
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
        _latestFocusTraceSequenceByAgent[requestedAgentId] = Math.Max(
            _latestFocusTraceSequenceByAgent.GetValueOrDefault(requestedAgentId),
            payload.LatestFocusTraceSequence);

        foreach (var trace in payload.NavigationTrace.OrderBy(static entry => entry.Sequence))
        {
            if (!string.Equals(trace.AgentId, requestedAgentId, StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = string.IsNullOrWhiteSpace(trace.Detail) ? string.Empty : $" ({trace.Detail})";
            _traceSawmill.Info("{0} -> {1}{2}", trace.Stage, trace.Outcome, suffix);
        }

        foreach (var trace in payload.FocusTrace.OrderBy(static entry => entry.Sequence))
        {
            if (!string.Equals(trace.AgentId, requestedAgentId, StringComparison.OrdinalIgnoreCase))
                continue;

            _focusSawmill.Info(
                "focus r{0} cseq={1}:{2} {3} -> {4} ({5})",
                trace.AttentionRevision,
                trace.CognitiveSequence,
                trace.OperationOrdinal,
                string.IsNullOrWhiteSpace(trace.PreviousTargetId) ? "<none>" : trace.PreviousTargetId,
                string.IsNullOrWhiteSpace(trace.CurrentTargetId) ? "<none>" : trace.CurrentTargetId,
                trace.Reason);
        }

        var tracedTargetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subscriber in _subscriberAgents)
        {
            if (string.Equals(subscriber.Value, requestedAgentId, StringComparison.OrdinalIgnoreCase)
                && _subscriberTargetIds.TryGetValue(subscriber.Key, out var targetId)
                && !string.IsNullOrWhiteSpace(targetId))
            {
                tracedTargetIds.Add(targetId);
            }
        }

        var message = ResolvePayload(payload, tracedTargetIds, bodyFrame);
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
            // deletion signal for resident belief targets that COGR no longer reports.
            RaiseNetworkEvent(message, subscriber.Channel);
        }
    }

    private COGRSpatialVisualizationMessage ResolvePayload(
        SpatialPollPayload payload,
        HashSet<string> tracedTargetIds,
        SpatialPollBodyFrame bodyFrame)
    {
        var empty = new COGRSpatialVisualizationMessage
        {
            AgentId = payload.AgentId,
            ResidentTargetCount = payload.ResidentTargetCount,
            RichlyMaintainedTargetCount = payload.RichlyMaintainedTargetCount,
            UnprojectableResidentTargetCount = payload.UnprojectableResidentTargetCount,
        };
        if (_subscribedConnection is not { IsConnected: true } connection
            || connection.ConnectionId == Guid.Empty)
        {
            return empty;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        if (!IsCapturedPollBodyFrameStillAuthoritative(connectionId, bodyFrame))
            return empty;

        var currentTick = (ulong)_timing.CurTick.Value;
        var bodyMapCoordinates = _transform.ToMapCoordinates(bodyFrame.Origin);
        if (bodyMapCoordinates.MapId == MapId.Nullspace)
            return empty;

        var targets = new List<COGRSpatialVisualizationTarget>();
        var tracedTargetsSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in payload.Targets)
        {
            var isTracedTarget = tracedTargetIds.Contains(target.TargetId);
            if (isTracedTarget)
            {
                tracedTargetsSeen.Add(target.TargetId);
                _spatialTraceSawmill.Info(
                    "runtime pollTick={0} responseTick={1} target={2} rev={3} focal={4} rich={5} local=({6:F4},{7:F4}) resident={8} unprojectable={9}",
                    bodyFrame.ObservedAtTick.Value,
                    currentTick,
                    target.TargetId,
                    target.TargetRevision,
                    target.IsFocal,
                    target.IsRichlyMaintained,
                    target.LocalX,
                    target.LocalY,
                    payload.ResidentTargetCount,
                    payload.UnprojectableResidentTargetCount);
            }

            if (!string.Equals(target.AgentId, payload.AgentId, StringComparison.OrdinalIgnoreCase)
                || !TryRealizeLocalPoint(
                    bodyFrame.Origin,
                    bodyFrame.LocalRotation,
                    target.LocalX,
                    target.LocalY,
                    target.LocalZ,
                    out var beliefCoordinates,
                    out var ownerRelativeNative,
                    out var parentOffset))
            {
                continue;
            }

            if (beliefCoordinates.MapId != bodyMapCoordinates.MapId)
                continue;

            var beliefRealizedMapDelta = beliefCoordinates.Position - bodyMapCoordinates.Position;
            var beliefExpectedDistanceTiles = parentOffset.Length();
            var beliefRealizedDistanceTiles = beliefRealizedMapDelta.Length();
            var beliefVectorMagnitudeLocalUnits = Math.Sqrt(
                (target.LocalX * target.LocalX)
                + (target.LocalY * target.LocalY)
                + (target.LocalZ * target.LocalZ));

            COGRSpatialCalibrationDiagnosticCache.PerceivedSpatialSample? perceivedSample = null;
            MapCoordinates? actualCoordinates = null;
            double? actualDistanceTiles = null;
            double? actualDistanceCalibratedLocalUnits = null;
            Vector2? actualMapDelta = null;

            if (Guid.TryParse(target.ActualEnvironmentReference, out var environmentGuid)
                && environmentGuid != Guid.Empty)
            {
                var environmentReference = EnvironmentRef.FromGuid(environmentGuid);
                COGRSpatialCalibrationDiagnosticCache.TryGet(bodyFrame.AgentId, environmentReference, out perceivedSample);

                var registry = _adapter.ReferenceRegistry;
                var actualEntity = registry?.TryResolve(
                    environmentReference,
                    new EnvironmentReferenceResolutionContext
                    {
                        ConnectionId = connectionId,
                        CurrentTick = new SimTick(currentTick),
                        BodyId = bodyFrame.BodyId,
                        BodyGeneration = bodyFrame.BodyGeneration,
                    });
                if (actualEntity.HasValue
                    && TryComp(actualEntity.Value, out TransformComponent? actualTransform))
                {
                    var resolvedActual = _transform.GetMapCoordinates(actualEntity.Value, xform: actualTransform);
                    if (resolvedActual.MapId != MapId.Nullspace
                        && resolvedActual.MapId == bodyMapCoordinates.MapId)
                    {
                        actualCoordinates = resolvedActual;
                        actualMapDelta = resolvedActual.Position - bodyMapCoordinates.Position;
                        actualDistanceTiles = actualMapDelta.Value.Length();
                        actualDistanceCalibratedLocalUnits = COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                            COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                            actualDistanceTiles.Value);
                    }
                }
            }

            var hasPerceivedSample = perceivedSample is not null;
            var perceivedSampleAgeTicks = hasPerceivedSample && currentTick >= perceivedSample!.ObservedTick
                ? currentTick - perceivedSample.ObservedTick
                : 0UL;

            if (isTracedTarget)
            {
                _spatialTraceSawmill.Info(
                    "station pollTick={0} responseTick={1} target={2} ego=({3:F4},{4:F4}) rot={5:F4} belief=({6:F4},{7:F4}) delta=({8:F4},{9:F4})",
                    bodyFrame.ObservedAtTick.Value,
                    currentTick,
                    target.TargetId,
                    bodyMapCoordinates.Position.X,
                    bodyMapCoordinates.Position.Y,
                    bodyFrame.LocalRotation.Theta,
                    beliefCoordinates.Position.X,
                    beliefCoordinates.Position.Y,
                    beliefRealizedMapDelta.X,
                    beliefRealizedMapDelta.Y);
            }

            targets.Add(new COGRSpatialVisualizationTarget
            {
                AgentId = target.AgentId,
                TargetId = target.TargetId,
                TargetRevision = target.TargetRevision,
                IsRichlyMaintained = target.IsRichlyMaintained,
                IsFocal = target.IsFocal,
                BodyOrigin = bodyMapCoordinates,
                Belief = beliefCoordinates,
                HasActual = actualCoordinates.HasValue,
                Actual = actualCoordinates.GetValueOrDefault(),
                BeliefLocalX = target.LocalX,
                BeliefLocalY = target.LocalY,
                BeliefOwnerRelativeNativeX = ownerRelativeNative.X,
                BeliefOwnerRelativeNativeY = ownerRelativeNative.Y,
                BodyLocalRotationRadians = bodyFrame.LocalRotation.Theta,
                BeliefParentOffsetX = parentOffset.X,
                BeliefParentOffsetY = parentOffset.Y,
                BeliefRealizedMapDeltaX = beliefRealizedMapDelta.X,
                BeliefRealizedMapDeltaY = beliefRealizedMapDelta.Y,
                BeliefExpectedDistanceTiles = beliefExpectedDistanceTiles,
                BeliefRealizedDistanceTiles = beliefRealizedDistanceTiles,
                HasPerceivedLocalRange = hasPerceivedSample,
                PerceivedLocalRange = perceivedSample?.LocalDistance ?? 0.0,
                HasPerceivedLocalVector = hasPerceivedSample,
                PerceivedLocalX = perceivedSample?.LocalX ?? 0.0,
                PerceivedLocalY = perceivedSample?.LocalY ?? 0.0,
                PerceivedSampleTick = perceivedSample?.ObservedTick ?? 0UL,
                PerceivedSampleAgeTicks = perceivedSampleAgeTicks,
                BeliefVectorMagnitudeLocalUnits = beliefVectorMagnitudeLocalUnits,
                HasActualDistanceTiles = actualDistanceTiles.HasValue,
                ActualDistanceTiles = actualDistanceTiles.GetValueOrDefault(),
                HasActualDistanceCalibratedLocalUnits = actualDistanceCalibratedLocalUnits.HasValue,
                ActualDistanceCalibratedLocalUnits = actualDistanceCalibratedLocalUnits.GetValueOrDefault(),
                HasActualMapDelta = actualMapDelta.HasValue,
                ActualMapDeltaX = actualMapDelta?.X ?? 0f,
                ActualMapDeltaY = actualMapDelta?.Y ?? 0f,
            });
        }

        foreach (var targetId in tracedTargetIds
                     .Where(targetId => !tracedTargetsSeen.Contains(targetId))
                     .OrderBy(static targetId => targetId, StringComparer.Ordinal))
        {
            _spatialTraceSawmill.Info(
                "runtime pollTick={0} responseTick={1} target={2} absent resident={3} unprojectable={4}",
                bodyFrame.ObservedAtTick.Value,
                currentTick,
                targetId,
                payload.ResidentTargetCount,
                payload.UnprojectableResidentTargetCount);
        }

        var paths = new List<COGRSpatialVisualizationPath>();
        foreach (var path in payload.Paths)
        {
            if (!string.Equals(path.AgentId, payload.AgentId, StringComparison.OrdinalIgnoreCase))
                continue;

            var points = new List<MapCoordinates>(path.Points.Length);
            foreach (var point in path.Points)
            {
                if (!TryRealizeLocalPoint(
                        bodyFrame.Origin,
                        bodyFrame.LocalRotation,
                        point.X,
                        point.Y,
                        point.Z,
                        out var realized,
                        out _,
                        out _))
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
            ResidentTargetCount = payload.ResidentTargetCount,
            RichlyMaintainedTargetCount = payload.RichlyMaintainedTargetCount,
            UnprojectableResidentTargetCount = payload.UnprojectableResidentTargetCount,
            Targets = targets.ToArray(),
            Paths = paths.ToArray(),
        };
    }

    private bool IsCapturedPollBodyFrameStillAuthoritative(
        ConnectionId connectionId,
        SpatialPollBodyFrame bodyFrame)
    {
        var lease = _authority.ResolveBoundLease(bodyFrame.AgentId, connectionId);
        if (!lease.HasValue
            || lease.Value.BodyId != bodyFrame.BodyId
            || lease.Value.Generation != bodyFrame.BodyGeneration)
        {
            return false;
        }

        var resolvedBody = _authority.ResolveBoundBody(
            bodyFrame.AgentId,
            bodyFrame.BodyId,
            connectionId,
            bodyFrame.BodyGeneration);
        if (!resolvedBody.HasValue
            || resolvedBody.Value != bodyFrame.BodyEntity
            || !TryComp(resolvedBody.Value, out TransformComponent? xform))
        {
            return false;
        }

        // A body reparent changes the physical coordinate frame in which the captured origin/rotation were expressed. Do not
        // reinterpret a historical local frame through a different parent merely to keep the debug marker visible.
        return xform.ParentUid == bodyFrame.Origin.EntityId;
    }

    private bool TryRealizeLocalPoint(
        EntityCoordinates origin,
        Angle localRotation,
        double localX,
        double localY,
        double localZ,
        out MapCoordinates realized,
        out Vector2 ownerRelativeNative,
        out Vector2 parentOffset)
    {
        realized = default;
        ownerRelativeNative = Vector2.Zero;
        parentOffset = Vector2.Zero;
        if (!double.IsFinite(localX)
            || !double.IsFinite(localY)
            || !double.IsFinite(localZ)
            || localZ != 0.0)
        {
            return false;
        }

        if (!COGREmbodimentSpatialProjection.TryOwnerRelativeLocalToParentCoordinates(
                origin.EntityId,
                origin.Position,
                localRotation,
                localX,
                localY,
                out var endpoint,
                out ownerRelativeNative,
                out parentOffset))
        {
            return false;
        }

        // The endpoint and ego origin are both retained in the exact parent-local frame sampled at poll time. Converting them
        // only when the response is displayed allows later common-mode grid/map motion, but later body-local motion cannot
        // silently move the diagnostic origin under a Runtime vector from an earlier causal epoch.
        realized = _transform.ToMapCoordinates(endpoint);
        return realized.MapId != MapId.Nullspace;
    }

    private void ClearDiagnosticState()
    {
        _latestPathSequenceByAgent.Clear();
        _latestNavigationTraceSequenceByAgent.Clear();
        _latestFocusTraceSequenceByAgent.Clear();
        _pollCursor = 0;
    }

    private static readonly JsonSerializerOptions SpatialJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly record struct SpatialPollBodyFrame(
        AgentId AgentId,
        BodyId BodyId,
        uint BodyGeneration,
        EntityUid BodyEntity,
        EntityCoordinates Origin,
        Angle LocalRotation,
        SimTick ObservedAtTick);

    private sealed class SpatialPollPayload
    {
        public bool Enabled { get; init; }
        public string AgentId { get; init; } = string.Empty;
        public ulong LatestPathSequence { get; init; }
        public ulong LatestNavigationTraceSequence { get; init; }
        public ulong LatestFocusTraceSequence { get; init; }
        public int ResidentTargetCount { get; init; }
        public int RichlyMaintainedTargetCount { get; init; }
        public int UnprojectableResidentTargetCount { get; init; }
        public SpatialTargetPayload[] Targets { get; init; } = [];
        public SpatialPathPayload[] Paths { get; init; } = [];
        public NavigationTracePayload[] NavigationTrace { get; init; } = [];
        public FocusTracePayload[] FocusTrace { get; init; } = [];
    }

    private sealed class SpatialTargetPayload
    {
        public string AgentId { get; init; } = string.Empty;
        public string TargetId { get; init; } = string.Empty;
        public ulong TargetRevision { get; init; }
        public bool IsRichlyMaintained { get; init; }
        public bool IsFocal { get; init; }
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

    private sealed class NavigationTracePayload
    {
        public ulong Sequence { get; init; }
        public string AgentId { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public string Outcome { get; init; } = string.Empty;
        public string? Detail { get; init; }
    }

    private sealed class FocusTracePayload
    {
        public ulong Sequence { get; init; }
        public string AgentId { get; init; } = string.Empty;
        public ulong AttentionRevision { get; init; }
        public ulong CognitiveSequence { get; init; }
        public uint OperationOrdinal { get; init; }
        public string? PreviousTargetId { get; init; }
        public string? CurrentTargetId { get; init; }
        public string Reason { get; init; } = string.Empty;
    }
}
