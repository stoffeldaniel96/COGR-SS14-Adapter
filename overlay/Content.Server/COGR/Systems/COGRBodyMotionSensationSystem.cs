using System;
using System.Collections.Generic;
using System.Numerics;
using COGR.Contracts.Messages;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using Content.Server.COGR;
using Content.Shared.COGR.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Transduces authoritative SS14 body motion into bounded vestibular/kinesthetic evidence for
/// COGR-controlled bodies.
/// </summary>
/// <remarks>
/// <para>
/// SS14 coordinates, native elapsed time, movement speed, event counts, maps, grids, routes, and
/// action identity remain adapter-private. A continuous movement interval is reduced to one
/// departure-body-relative bearing, one saturated duration band, one calibrated body-schema
/// displacement estimate when translation is present, coarse reorientation, and bounded
/// simulation-order provenance before it enters cognition.
/// </para>
/// <para>
/// This is a passive body-sensory path, not a motor-control path. Voluntary movement blockers are
/// deliberately not consulted: being unable to initiate locomotion does not imply loss of
/// vestibular, contact, or kinesthetic sensation when the body is moved by dragging, pushing,
/// following, buckling, or another external cause.
/// </para>
/// <para>
/// MoveEvent frequency is never forwarded or counted as distance. Continuous native deltas are
/// aggregated only inside the adapter, transformed into the departure-body frame, and calibrated
/// into the same body-schema local units used by visual spatial projection. The rich displacement
/// channel carries that transform-derived geometry without adapter-invented uncertainty; Runtime
/// cognition owns interpretation and cognitive fallibility. Host elapsed time is used only to
/// choose a bounded qualitative duration category and to bound sensory publication cadence.
/// Simulation ticks cross only as temporal provenance so Runtime can order bodily sensation against
/// visual evidence without reconstructing a host pose.
/// </para>
/// </remarks>
public sealed partial class COGRBodyMotionSensationSystem : EntitySystem
{
    private const float TranslationNoiseFloorSquared = 0.000025f;
    private const float DiscontinuousTranslationSquared = 16f;
    private const double RotationNoiseFloorRadians = Math.PI / 360d;
    private const int DirectionChangeFlushSectors = 2;
    private const int RotationChangeFlushOctants = 2;

    // The rich V3 displacement is an exact adapter transduction into COGR body-local units, not an
    // odometer or world pose. Contract fixed-point encoding may bound transport precision, but the
    // adapter does not add psychophysical quantization or uncertainty that the embodiment does not
    // actually provide. Cognitive uncertainty is applied after ingress by Runtime faculties.
    private const int TranslationTransductionUncertaintyMillionths = 0;

    // Ongoing embodied motion is a sensory stream. Bound aggregation near 8 Hz so current owner-frame
    // estimates can advance repeatedly during locomotion without coupling publication to MoveEvent
    // count or creating a Runtime cognitive tick. A short quiet flush preserves the terminal fragment.
    private static readonly TimeSpan MotionQuietPeriod = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumMotionInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan MomentaryMaximum = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BriefMaximum = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan SustainedMaximum = TimeSpan.FromMilliseconds(2500);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private MobStateSystem _mobState = default!;

    private readonly Dictionary<EntityUid, MotionAuthorityKey> _continuity = new();
    private readonly Dictionary<EntityUid, PendingMotion> _pendingMotion = new();
    private readonly HashSet<EntityUid> _pendingBaselines = new();
    private readonly List<EntityUid> _readyBodies = new();

    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private ISawmill _sawmill = default!;
    private bool _authorityContextObserved;
    private WorldId? _observedWorld;
    private ConnectionId? _observedConnection;

    public override void Initialize()
    {
        base.Initialize();
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _sawmill = _logManager.GetSawmill("cogr.body-motion-sensation");

        // COGRRegionalPerceptionRouterSystem owns the directed COGRControlledComponent + MoveEvent
        // subscription and fans raw controlled-body movement into this sensory transducer.
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
    }

    public override void Shutdown()
    {
        ClearAllContinuity();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        SynchronizeAuthorityContext();
        PublishReadyBaselines();
        FlushReadyMotionIntervals();
    }

    /// <summary>
    /// Establishes a new passive body-motion continuity stream immediately after the authority
    /// coordinator has verified the exact connection/body/generation lease.
    /// </summary>
    public void NotifyControlledBodyAuthorityBound(EntityUid uid, COGRControlledComponent controlled)
    {
        // The authority coordinator calls this only after BoundWorld/BoundConnection and the exact
        // body generation are established. Mark that context as already observed so our next Update
        // cannot clear the baseline that this same authority edge is about to publish.
        _authorityContextObserved = true;
        _observedWorld = _authority.BoundWorld;
        _observedConnection = _authority.BoundConnection;
        ResetContinuity(uid, controlled, "authority_bound");
    }

    /// <summary>
    /// Clears adapter-private aggregation state before one controlled body or its authority is
    /// removed. No terminal movement sample is manufactured across an authority boundary.
    /// </summary>
    public void NotifyControlledBodyAuthorityRemoved(EntityUid uid)
    {
        ClearBody(uid);
    }

    /// <summary>
    /// Closes any pending bodily-motion sensation immediately before a visual scene is sampled from
    /// this observer. This creates an explicit temporal partition: the fresh visual frame supersedes
    /// every motion interval ending at or before its observation tick, while later bodily motion can
    /// re-express that frame normally. No visual or spatial truth is fed back into proprioception.
    /// </summary>
    public void NotifyVisualSamplingBoundary(EntityUid uid)
    {
        FlushPendingMotion(uid, "visual_sampling_boundary");
    }

    /// <summary>
    /// Consumes one authoritative controlled-body movement event from the regional movement-event
    /// owner. The event remains adapter-private and is reduced to bounded body-relative sensation.
    /// </summary>
    public void NotifyControlledBodyMoved(
        EntityUid uid,
        COGRControlledComponent controlled,
        ref MoveEvent args)
    {
        if (!controlled.IsActive)
        {
            ClearBody(uid);
            return;
        }

        if (args.ParentChanged)
        {
            FlushPendingMotion(uid, "reference_frame_change");
            ResetContinuity(uid, controlled, "reference_frame_change");
            return;
        }

        var parentDelta = args.NewPosition.Position - args.OldPosition.Position;
        var rotationDelta = SignedCognitiveAngleDelta(args.OldRotation, args.NewRotation);
        var hasTranslation = parentDelta.LengthSquared() >= TranslationNoiseFloorSquared;
        var hasRotation = Math.Abs(rotationDelta) >= RotationNoiseFloorRadians;
        if (!hasTranslation && !hasRotation)
            return;

        // A single same-parent jump far beyond plausible continuous body motion is treated as a
        // sensory discontinuity rather than traversed path. This deliberately errs toward losing
        // one interval instead of manufacturing route geometry from teleport/admin relocation.
        if (hasTranslation && parentDelta.LengthSquared() >= DiscontinuousTranslationSquared)
        {
            FlushPendingMotion(uid, "discontinuous_translation");
            ResetContinuity(uid, controlled, "discontinuous_translation");
            return;
        }

        if (!TryGetMotionContext(uid, controlled, out var context))
        {
            ClearBody(uid);
            if (IsMotionSenseViable(uid))
                _pendingBaselines.Add(uid);
            return;
        }

        if (!_continuity.TryGetValue(uid, out var established) || established != context.Key)
        {
            _pendingMotion.Remove(uid);
            PublishBaseline(uid, context, "authority_baseline");
            return;
        }

        var now = _timing.CurTime;
        var nowTick = new SimTick((ulong)_timing.CurTick.Value);
        if (!_pendingMotion.TryGetValue(uid, out var pending) ||
            pending.Authority != context.Key ||
            pending.Parent != args.OldPosition.EntityId)
        {
            pending = new PendingMotion(
                context.Key,
                args.OldPosition.EntityId,
                args.OldRotation,
                now,
                nowTick);
            _pendingMotion[uid] = pending;
        }

        var translated = ProjectIntoDepartureBodyFrame(parentDelta, pending.DepartureRotation);
        var instantaneousBearing = hasTranslation
            ? QuantizeBearing(translated)
            : BodyRelativeBearing.Unknown;

        if (hasTranslation &&
            pending.HasTranslation &&
            IsDirectional(pending.LastInstantaneousBearing) &&
            IsDirectional(instantaneousBearing) &&
            BearingSectorDistance(pending.LastInstantaneousBearing, instantaneousBearing) >= DirectionChangeFlushSectors)
        {
            FlushPendingMotion(uid, "direction_change");
            pending = new PendingMotion(
                context.Key,
                args.OldPosition.EntityId,
                args.OldRotation,
                now,
                nowTick);
            _pendingMotion[uid] = pending;
            translated = ProjectIntoDepartureBodyFrame(parentDelta, pending.DepartureRotation);
            instantaneousBearing = QuantizeBearing(translated);
        }

        if (hasTranslation)
        {
            pending.DepartureBodyTranslation += translated;
            pending.HasTranslation = true;
            if (IsDirectional(instantaneousBearing))
                pending.LastInstantaneousBearing = instantaneousBearing;
        }

        if (hasRotation)
            pending.AccumulatedRotationRadians += rotationDelta;

        pending.LastObservedAt = now;
        pending.LastObservedTick = nowTick;

        var rotationOctants = QuantizeRotationOctants(pending.AccumulatedRotationRadians);
        if (Math.Abs(rotationOctants) >= RotationChangeFlushOctants ||
            now - pending.FirstObservedAt >= MaximumMotionInterval)
        {
            FlushPendingMotion(
                uid,
                Math.Abs(rotationOctants) >= RotationChangeFlushOctants
                    ? "coarse_reorientation"
                    : "maximum_interval");
        }
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (!TryComp<COGRControlledComponent>(args.Target, out var controlled) || !controlled.IsActive)
            return;

        var wasViable = IsMotionSenseViable(args.OldMobState);
        var isViable = IsMotionSenseViable(args.NewMobState);
        if (wasViable == isViable)
            return;

        // Alive <-> Critical is ordinary body-state variation and does not sever vestibular
        // continuity. Death/invalidity does; later restoration begins a new body-sensory stream.
        if (!isViable)
        {
            ClearBody(args.Target);
            return;
        }

        ResetContinuity(args.Target, controlled, "body_viability_restored");
    }

    private void SynchronizeAuthorityContext()
    {
        var world = _authority.BoundWorld;
        var connection = _authority.BoundConnection;
        if (_authorityContextObserved &&
            Nullable.Equals(_observedWorld, world) &&
            Nullable.Equals(_observedConnection, connection))
        {
            return;
        }

        _authorityContextObserved = true;
        _observedWorld = world;
        _observedConnection = connection;
        ClearAllContinuity();

        if (!world.HasValue || !connection.HasValue)
            return;

        var query = EntityQueryEnumerator<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var controlled))
        {
            if (controlled.IsActive && IsMotionSenseViable(uid))
                _pendingBaselines.Add(uid);
        }
    }

    private void PublishReadyBaselines()
    {
        if (_pendingBaselines.Count == 0)
            return;

        _readyBodies.Clear();
        foreach (var uid in _pendingBaselines)
            _readyBodies.Add(uid);

        foreach (var uid in _readyBodies)
        {
            if (!TryComp<COGRControlledComponent>(uid, out var controlled) ||
                !controlled.IsActive ||
                !IsMotionSenseViable(uid))
            {
                ClearBody(uid);
                continue;
            }

            if (!TryGetMotionContext(uid, controlled, out var context))
                continue;

            PublishBaseline(uid, context, "authority_available");
            _pendingBaselines.Remove(uid);
        }
    }

    private void FlushReadyMotionIntervals()
    {
        if (_pendingMotion.Count == 0)
            return;

        var now = _timing.CurTime;
        _readyBodies.Clear();
        foreach (var (uid, pending) in _pendingMotion)
        {
            if (now - pending.LastObservedAt >= MotionQuietPeriod ||
                now - pending.FirstObservedAt >= MaximumMotionInterval)
            {
                _readyBodies.Add(uid);
            }
        }

        foreach (var uid in _readyBodies)
        {
            FlushPendingMotion(
                uid,
                _pendingMotion.TryGetValue(uid, out var pending) &&
                now - pending.FirstObservedAt >= MaximumMotionInterval
                    ? "maximum_interval"
                    : "motion_quiet");
        }
    }

    private void FlushPendingMotion(EntityUid uid, string reason)
    {
        if (!_pendingMotion.Remove(uid, out var pending))
            return;
        if (!TryComp<COGRControlledComponent>(uid, out var controlled) ||
            !TryGetMotionContext(uid, controlled, out var context) ||
            context.Key != pending.Authority ||
            !_continuity.TryGetValue(uid, out var established) ||
            established != context.Key)
        {
            _continuity.Remove(uid);
            if (IsMotionSenseViable(uid))
                _pendingBaselines.Add(uid);
            return;
        }

        var rotationOctants = QuantizeRotationOctants(pending.AccumulatedRotationRadians);
        var bearing = pending.HasTranslation
            ? QuantizeBearing(pending.DepartureBodyTranslation)
            : BodyRelativeBearing.Unknown;
        if (pending.HasTranslation && !IsDirectional(bearing))
            bearing = pending.LastInstantaneousBearing;

        var hasQualitativeTranslation = pending.HasTranslation && IsDirectional(bearing);
        if (!hasQualitativeTranslation && rotationOctants == 0)
            return;

        var duration = hasQualitativeTranslation
            ? ClassifyDuration(pending.LastObservedAt - pending.FirstObservedAt)
            : ProprioceptiveMotionDurationBand.None;
        var translationEstimate = hasQualitativeTranslation
            ? CreateTranslationEstimate(pending.DepartureBodyTranslation)
            : null;

        PublishEvidence(
            context,
            establishesContinuity: false,
            bearing,
            duration,
            translationEstimate,
            rotationOctants,
            pending.FirstObservedTick,
            pending.LastObservedTick,
            reason);
    }

    private void ResetContinuity(
        EntityUid uid,
        COGRControlledComponent controlled,
        string reason)
    {
        _pendingMotion.Remove(uid);
        _continuity.Remove(uid);
        _pendingBaselines.Remove(uid);

        if (TryGetMotionContext(uid, controlled, out var context))
            PublishBaseline(uid, context, reason);
        else if (IsMotionSenseViable(uid))
            _pendingBaselines.Add(uid);
    }

    private void PublishBaseline(EntityUid uid, MotionContext context, string reason)
    {
        PublishEvidence(
            context,
            establishesContinuity: true,
            BodyRelativeBearing.Unknown,
            ProprioceptiveMotionDurationBand.None,
            translationEstimate: null,
            rotationOctants: 0,
            motionStartedAtTick: null,
            motionEndedAtTick: null,
            reason);
        _continuity[uid] = context.Key;
    }

    private void PublishEvidence(
        MotionContext context,
        bool establishesContinuity,
        BodyRelativeBearing translationBearing,
        ProprioceptiveMotionDurationBand translationDuration,
        ProprioceptiveOwnerFrameTranslationEstimate? translationEstimate,
        int rotationOctants,
        SimTick? motionStartedAtTick,
        SimTick? motionEndedAtTick,
        string reason)
    {
        var emittedAtTick = new SimTick((ulong)_timing.CurTick.Value);
        var evidence = new ProprioceptiveOwnerFrameMotionEvidence
        {
            BodyId = context.BodyId,
            BodyGeneration = context.BodyGeneration,
            EstablishesContinuity = establishesContinuity,
            TranslationBearing = translationBearing,
            TranslationDuration = translationDuration,
            TranslationEstimate = translationEstimate,
            RotationOctants = rotationOctants,
            MotionStartedAtTick = motionStartedAtTick,
            MotionEndedAtTick = motionEndedAtTick,
        };

        context.Connection.EnqueueEnvironmentMessage(new PerceptionMessage
        {
            WorldId = context.WorldId,
            ConnectionId = context.ConnectionId,
            Tick = emittedAtTick,
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            AgentId = context.AgentId,
            PerceptId = PerceptId.NewId(),
            Category = PerceptionCategory.Proprioceptive,
            Data = ProprioceptiveOwnerFrameMotionEvidenceWireCodec.Encode(evidence),
            Format = ProprioceptiveOwnerFrameMotionEvidenceWireCodec.Format,
        });

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[COGR][ProprioceptiveOwnerFrameMotionEmitted] agent={0} body={1} generation={2} baseline={3} bearing={4} duration={5} translation_estimate={6} rotation_octants={7} motion_start_tick={8} motion_end_tick={9} emitted_tick={10} reason={11}",
                context.AgentId,
                context.BodyId,
                context.BodyGeneration,
                establishesContinuity,
                translationBearing,
                translationDuration,
                FormatTranslationEstimate(translationEstimate),
                rotationOctants,
                motionStartedAtTick?.Value,
                motionEndedAtTick?.Value,
                emittedAtTick.Value,
                reason);
        }
    }

    private bool TryGetMotionContext(
        EntityUid uid,
        COGRControlledComponent controlled,
        out MotionContext context)
    {
        context = default;
        if (!controlled.IsActive ||
            controlled.AgentId == Guid.Empty ||
            controlled.BodyId == Guid.Empty ||
            !IsMotionSenseViable(uid) ||
            _adapter.Connection is not { IsConnected: true } connection ||
            connection.ConnectionId == Guid.Empty ||
            !_authority.BoundWorld.HasValue ||
            !_authority.BoundConnection.HasValue)
        {
            return false;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        if (_authority.BoundConnection.Value != connectionId)
            return false;

        var agentId = AgentId.FromGuid(controlled.AgentId);
        var bodyId = BodyId.FromGuid(controlled.BodyId);
        var lease = _authority.ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue ||
            lease.Value.BodyId != bodyId ||
            lease.Value.Generation == 0 ||
            !_authority.ResolveBoundBody(agentId, bodyId, connectionId, lease.Value.Generation).HasValue)
        {
            return false;
        }

        context = new MotionContext(
            connection,
            _authority.BoundWorld.Value,
            connectionId,
            agentId,
            bodyId,
            lease.Value.Generation);
        return true;
    }

    private void ClearBody(EntityUid uid)
    {
        _continuity.Remove(uid);
        _pendingMotion.Remove(uid);
        _pendingBaselines.Remove(uid);
    }

    private void ClearAllContinuity()
    {
        _continuity.Clear();
        _pendingMotion.Clear();
        _pendingBaselines.Clear();
        _readyBodies.Clear();
    }

    private bool IsMotionSenseViable(EntityUid uid) => _mobState.IsAlive(uid) || _mobState.IsCritical(uid);

    private static bool IsMotionSenseViable(MobState state) => state is MobState.Alive or MobState.Critical;

    private static Vector2 ProjectIntoDepartureBodyFrame(Vector2 parentDelta, Angle departureRotation)
    {
        // Match the established COGR visual owner-frame convention: +X is forward and +Y is left.
        // Exact native components remain inside Station and are reduced to actor-relative sensation
        // before emission.
        var theta = departureRotation.Theta;
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        var forward = (parentDelta.X * cos) + (parentDelta.Y * sin);
        var left = (-parentDelta.X * sin) + (parentDelta.Y * cos);
        return new Vector2((float)forward, (float)left);
    }

    private static ProprioceptiveOwnerFrameTranslationEstimate? CreateTranslationEstimate(
        Vector2 departureBodyNative)
    {
        if (!float.IsFinite(departureBodyNative.X) || !float.IsFinite(departureBodyNative.Y))
            return null;

        try
        {
            var forwardBodyLengths = COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                departureBodyNative.X);
            var leftBodyLengths = COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                departureBodyNative.Y);

            return new ProprioceptiveOwnerFrameTranslationEstimate(
                BodyRelativeSpatialComponent.FromBodyLengths(forwardBodyLengths),
                BodyRelativeSpatialComponent.FromBodyLengths(leftBodyLengths),
                0,
                new PerceptualSpatialUncertainty(TranslationTransductionUncertaintyMillionths));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string FormatTranslationEstimate(
        ProprioceptiveOwnerFrameTranslationEstimate? estimate)
    {
        if (estimate is not { } value)
            return "none";

        return $"({BodyRelativeSpatialComponent.FormatBodyLengths(value.Forward)}," +
               $"{BodyRelativeSpatialComponent.FormatBodyLengths(value.Left)}," +
               $"{BodyRelativeSpatialComponent.FormatBodyLengths(value.Up)};u=" +
               $"{value.Uncertainty.Millionths})";
    }

    private static BodyRelativeBearing QuantizeBearing(Vector2 departureBodyDelta)
    {
        if (departureBodyDelta.LengthSquared() < TranslationNoiseFloorSquared)
            return BodyRelativeBearing.Unknown;

        var angle = Math.Atan2(departureBodyDelta.Y, departureBodyDelta.X);
        var sector = (int)Math.Round(
            angle / (Math.PI / 4d),
            MidpointRounding.AwayFromZero);
        sector %= 8;
        if (sector < 0)
            sector += 8;

        return sector switch
        {
            0 => BodyRelativeBearing.Forward,
            1 => BodyRelativeBearing.ForwardLeft,
            2 => BodyRelativeBearing.Left,
            3 => BodyRelativeBearing.BackLeft,
            4 => BodyRelativeBearing.Back,
            5 => BodyRelativeBearing.BackRight,
            6 => BodyRelativeBearing.Right,
            7 => BodyRelativeBearing.ForwardRight,
            _ => BodyRelativeBearing.Unknown,
        };
    }

    private static int BearingSectorDistance(BodyRelativeBearing first, BodyRelativeBearing second)
    {
        var firstSector = BearingSector(first);
        var secondSector = BearingSector(second);
        if (firstSector < 0 || secondSector < 0)
            return 0;

        var difference = Math.Abs(firstSector - secondSector);
        return Math.Min(difference, 8 - difference);
    }

    private static int BearingSector(BodyRelativeBearing bearing) => bearing switch
    {
        BodyRelativeBearing.Forward => 0,
        BodyRelativeBearing.ForwardLeft => 1,
        BodyRelativeBearing.Left => 2,
        BodyRelativeBearing.BackLeft => 3,
        BodyRelativeBearing.Back => 4,
        BodyRelativeBearing.BackRight => 5,
        BodyRelativeBearing.Right => 6,
        BodyRelativeBearing.ForwardRight => 7,
        _ => -1,
    };

    private static bool IsDirectional(BodyRelativeBearing bearing) => BearingSector(bearing) >= 0;

    private static double SignedCognitiveAngleDelta(Angle previous, Angle current)
    {
        // Robust/Station positive native Theta rotates local +X toward parent +Y. Under the established
        // COGR body frame (+X forward, +Y left), that is a leftward/counter-clockwise owner turn.
        // COGR proprioceptive RotationOctants deliberately use the opposite sign: positive is a
        // rightward/clockwise owner turn. Translate handedness here at the environment boundary.
        var nativeDelta = current.Theta - previous.Theta;
        while (nativeDelta <= -Math.PI)
            nativeDelta += Math.Tau;
        while (nativeDelta > Math.PI)
            nativeDelta -= Math.Tau;
        return -nativeDelta;
    }

    private static int QuantizeRotationOctants(double rotationRadians)
    {
        var octants = (int)Math.Round(
            rotationRadians / (Math.PI / 4d),
            MidpointRounding.AwayFromZero);
        octants %= 8;
        if (octants < -3)
            octants += 8;
        else if (octants > 4)
            octants -= 8;
        return octants;
    }

    private static ProprioceptiveMotionDurationBand ClassifyDuration(TimeSpan duration)
    {
        if (duration <= MomentaryMaximum)
            return ProprioceptiveMotionDurationBand.Momentary;
        if (duration <= BriefMaximum)
            return ProprioceptiveMotionDurationBand.Brief;
        if (duration <= SustainedMaximum)
            return ProprioceptiveMotionDurationBand.Sustained;
        return ProprioceptiveMotionDurationBand.Extended;
    }

    private readonly record struct MotionAuthorityKey(
        ConnectionId ConnectionId,
        AgentId AgentId,
        BodyId BodyId,
        uint BodyGeneration);

    private readonly record struct MotionContext(
        COGRConnectionManager Connection,
        WorldId WorldId,
        ConnectionId ConnectionId,
        AgentId AgentId,
        BodyId BodyId,
        uint BodyGeneration)
    {
        public MotionAuthorityKey Key => new(ConnectionId, AgentId, BodyId, BodyGeneration);
    }

    private sealed class PendingMotion(
        MotionAuthorityKey authority,
        EntityUid parent,
        Angle departureRotation,
        TimeSpan firstObservedAt,
        SimTick firstObservedTick)
    {
        public MotionAuthorityKey Authority { get; } = authority;
        public EntityUid Parent { get; } = parent;
        public Angle DepartureRotation { get; } = departureRotation;
        public TimeSpan FirstObservedAt { get; } = firstObservedAt;
        public TimeSpan LastObservedAt { get; set; } = firstObservedAt;
        public SimTick FirstObservedTick { get; } = firstObservedTick;
        public SimTick LastObservedTick { get; set; } = firstObservedTick;
        public Vector2 DepartureBodyTranslation { get; set; }
        public double AccumulatedRotationRadians { get; set; }
        public bool HasTranslation { get; set; }
        public BodyRelativeBearing LastInstantaneousBearing { get; set; } = BodyRelativeBearing.Unknown;
    }
}
