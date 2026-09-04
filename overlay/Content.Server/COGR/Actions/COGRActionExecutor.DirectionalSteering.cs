using System.Linq;
using System.Numerics;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Time;
using Content.Server.NPC.Components;
using Content.Server.NPC.Events;
using Content.Server.NPC.Systems;
using Content.Shared.COGR.Components;
using Content.Shared.Movement.Components;
using Content.Shared.NPC;

namespace Content.Server.COGR.Actions;

public sealed partial class COGRActionExecutor
{
    private const ulong DirectionalSteeringProgressCheckTicks = 30;
    private const float DirectionalSteeringMinimumForwardProgress = 0.05f;
    private const int DirectionalSteeringMaximumStallChecks = 6;
    private const float OctantHalfWidthCosine = 0.9238795f; // cos(22.5 degrees)

    [Dependency] private NPCSteeringSystem _npcSteering = default!;

    private readonly Dictionary<ActionProposalId, ActiveDirectionalSteering> _directionalSteering = new();
    private readonly Dictionary<EntityUid, ActionProposalId> _directionalSteeringByEntity = new();

    private void InitializeDirectionalSteering()
    {
        SubscribeLocalEvent<COGRControlledComponent, NPCSteeringEvent>(OnCogRDirectionalSteering);
    }

    private ActionExecutionResult StartDirectionalSteering(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<SteerRelativeActionParams>(attempt.Parameters);
        if (parameters is null
            || !TryResolveOwnerRelativeSteeringDirection(parameters, out var ownerRelativeDirection, out var directionMode))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Invalid relative steering parameters: supply a finite non-zero continuous direction, a directional octant bearing, or both");
        }

        var entity = ResolveDirectionalSteeringBody(attempt.BodyId);
        if (!entity.HasValue)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.BodyDied,
                "Body entity not found");
        }

        if (!TryComp(entity.Value, out TransformComponent? xform))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.BodyDied,
                "Body has no transform");
        }

        var parentDirection = OwnerRelativeToParentSteeringDirection(ownerRelativeDirection, xform.LocalRotation);
        if (!float.IsFinite(parentDirection.X)
            || !float.IsFinite(parentDirection.Y)
            || parentDirection == Vector2.Zero)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Relative steering direction has no finite planar realization");
        }

        var requestedProgress = parameters.MaximumDistance ?? COGRSpatialPolicy.MaximumDirectionalSteeringProgress;
        if (!double.IsFinite(requestedProgress) || requestedProgress <= 0)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Relative steering progress horizon must be finite and positive");
        }

        var progressHorizon = (float)Math.Min(
            requestedProgress,
            COGRSpatialPolicy.MaximumDirectionalSteeringProgress);

        EnsureComp<InputMoverComponent>(entity.Value);
        EnsureComp<MovementSpeedModifierComponent>(entity.Value);
        EnsureComp<MobMoverComponent>(entity.Value);
        EnsureComp<ActiveNPCComponent>(entity.Value);

        // A direction-only objective deliberately has no destination coordinate. Register the current coordinates only as
        // the NPC steering system's required live-body anchor; the event override below disables coordinate seeking and
        // contributes the cognition-owned direction directly to native context steering alongside collision/separation
        // danger. Continuous input remains continuous. A supplied octant is either a cheap standalone steering intent or a
        // coarse sector constraint on the continuous vector; neither form requires body facing to equal locomotion direction.
        _npcSteering.Unregister(entity.Value);
        EnsureComp<ActiveNPCComponent>(entity.Value);
        var steering = _npcSteering.Register(entity.Value, xform.Coordinates);
        steering.Status = SteeringStatus.Moving;

        var startTick = (ulong)_timing.CurTick.Value;
        var active = new ActiveDirectionalSteering
        {
            ProposalId = attempt.ProposalId,
            BodyId = attempt.BodyId,
            Entity = entity.Value,
            ParentUid = xform.ParentUid,
            StartPosition = xform.LocalPosition,
            ParentDirection = Vector2.Normalize(parentDirection),
            ProgressHorizon = progressHorizon,
            LastProgressCheckTick = startTick,
        };

        if (COGRAdapterTrace.Enabled)
        {
            var diagnosticHorizonPoint = active.StartPosition + active.ParentDirection * active.ProgressHorizon;
            _sawmill.Debug(
                "COGR steer: proposal={0} agent={1} mode={2} bearing={3} ownerDirection=({4:F3},{5:F3}) parentDirection=({6:F3},{7:F3}) horizon={8:F3} endpoint=({9:F3},{10:F3})",
                attempt.ProposalId,
                attempt.AgentId,
                directionMode,
                parameters.Bearing,
                ownerRelativeDirection.X,
                ownerRelativeDirection.Y,
                active.ParentDirection.X,
                active.ParentDirection.Y,
                active.ProgressHorizon,
                diagnosticHorizonPoint.X,
                diagnosticHorizonPoint.Y);
        }

        _directionalSteering[attempt.ProposalId] = active;
        _directionalSteeringByEntity[entity.Value] = attempt.ProposalId;
        return ActionExecutionResult.Started();
    }

    private void OnCogRDirectionalSteering(
        EntityUid uid,
        COGRControlledComponent component,
        ref NPCSteeringEvent args)
    {
        if (!_directionalSteeringByEntity.TryGetValue(uid, out var proposalId)
            || !_directionalSteering.TryGetValue(proposalId, out var active))
        {
            return;
        }

        // ParentDirection is already in the same grid/input frame used by NPCSteeringSystem.Directions. Applying
        // args.OffsetRotation again would rotate the cognitive direction twice on rotated grids.
        var desired = active.ParentDirection;
        for (var index = 0; index < SharedNPCSteeringSystem.InterestDirections; index++)
        {
            // Mirror NPCSteeringSystem.ApplySeek: the continuous desired vector supplies interest while native collision
            // avoidance and separation remain authoritative contributors. This is steering evidence, not a hidden coordinate.
            var dot = Vector2.Dot(desired, NPCSteeringSystem.Directions[index]);
            var interest = Math.Clamp((dot + 1f) * 0.5f, 0f, 1f);
            args.Steering.Interest[index] = MathF.Max(args.Steering.Interest[index], interest);
        }

        args.Steering.CanSeek = false;
    }

    private IReadOnlyList<ActionResult> TickDirectionalSteering(ulong currentTick)
    {
        if (_directionalSteering.Count == 0)
            return Array.Empty<ActionResult>();

        var results = new List<ActionResult>();
        foreach (var active in _directionalSteering.Values.ToArray())
        {
            var result = TickDirectionalSteering(active, currentTick);
            if (result is not null)
                results.Add(result);
        }

        return results;
    }

    private ActionResult? TickDirectionalSteering(
        ActiveDirectionalSteering active,
        ulong currentTick)
    {
        var attempt = _actionRegistry.GetAction(active.ProposalId);
        if (attempt is null)
        {
            CleanupDirectionalSteering(active.ProposalId);
            return null;
        }

        var tick = new SimTick(currentTick);
        if (!Exists(active.Entity))
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.BodyDied,
                "Body entity was deleted during directional steering");
        }

        var currentBody = ResolveDirectionalSteeringBody(active.BodyId);
        if (currentBody != active.Entity)
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.BodyReplaced,
                "Body authority now resolves to a different entity");
        }

        if (!TryComp(active.Entity, out TransformComponent? xform)
            || xform.ParentUid != active.ParentUid)
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.NoPathFound,
                "Directional steering spatial frame changed before reevaluation");
        }

        if (!TryComp<NPCSteeringComponent>(active.Entity, out var steering)
            || steering.Status == SteeringStatus.NoPath)
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.NoPathFound,
                "Native directional steering cannot make progress");
        }

        var forwardProgress = Vector2.Dot(
            xform.LocalPosition - active.StartPosition,
            active.ParentDirection);
        if (forwardProgress > active.BestForwardProgress)
            active.BestForwardProgress = forwardProgress;

        if (active.BestForwardProgress >= active.ProgressHorizon)
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Completed, tick);
            return ActionResult.Completed(
                active.ProposalId,
                tick,
                detail: "Bounded relative steering progress reached; cognition should reassess current scene evidence");
        }

        if (currentTick - active.LastProgressCheckTick >= DirectionalSteeringProgressCheckTicks)
        {
            var gained = active.BestForwardProgress - active.LastObservedBestForwardProgress;
            if (gained < DirectionalSteeringMinimumForwardProgress)
            {
                active.ConsecutiveStallChecks++;
                if (active.ConsecutiveStallChecks >= DirectionalSteeringMaximumStallChecks)
                {
                    CleanupDirectionalSteering(active.ProposalId);
                    _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
                    return ActionResult.Failed(
                        active.ProposalId,
                        tick,
                        ActionFailureReason.NoPathFound,
                        "Directional steering made no net progress along the requested direction");
                }
            }
            else
            {
                active.ConsecutiveStallChecks = 0;
            }

            active.LastObservedBestForwardProgress = active.BestForwardProgress;
            active.LastProgressCheckTick = currentTick;
        }

        if (attempt.State == ActionState.Started)
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Progressing, tick);

        return null;
    }

    private void CleanupDirectionalSteering(ActionProposalId proposalId)
    {
        if (!_directionalSteering.Remove(proposalId, out var active))
            return;

        _directionalSteeringByEntity.Remove(active.Entity);
        if (Exists(active.Entity))
        {
            _npcSteering.Unregister(active.Entity);
            RemComp<ActiveNPCComponent>(active.Entity);
            if (TryComp<InputMoverComponent>(active.Entity, out var mover))
            {
                mover.CurTickSprintMovement = Vector2.Zero;
                mover.CurTickWalkMovement = Vector2.Zero;
                Dirty(active.Entity, mover);
            }
        }
    }

    private EntityUid? ResolveDirectionalSteeringBody(BodyId bodyId)
    {
        var query = AllEntityQuery<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var controlled))
        {
            if (controlled.BodyId == bodyId.ToGuid() && controlled.IsActive)
                return uid;
        }

        return null;
    }

    private static bool TryResolveOwnerRelativeSteeringDirection(
        SteerRelativeActionParams parameters,
        out Vector2 direction,
        out string mode)
    {
        direction = Vector2.Zero;
        mode = "invalid";

        var hasBearing = BodyRelativeBearingProjection.IsDirectional(parameters.Bearing);
        if (parameters.Bearing != BodyRelativeBearing.Unknown && !hasBearing)
            return false;

        var hasContinuous = parameters.Direction.HasValue;
        if (hasContinuous && !parameters.Direction.Value.IsDirectional)
            return false;

        if (!hasContinuous && !hasBearing)
            return false;

        var bearingDirection = hasBearing
            ? BearingToOwnerRelativeSteeringDirection(parameters.Bearing)
            : Vector2.Zero;

        if (!hasContinuous)
        {
            direction = bearingDirection;
            mode = "octant";
            return direction != Vector2.Zero;
        }

        var continuous = new Vector2(
            (float)parameters.Direction!.Value.Forward,
            (float)parameters.Direction.Value.Left);
        if (!float.IsFinite(continuous.X)
            || !float.IsFinite(continuous.Y)
            || continuous == Vector2.Zero)
        {
            return false;
        }

        continuous = Vector2.Normalize(continuous);
        if (!hasBearing)
        {
            direction = continuous;
            mode = "continuous";
            return true;
        }

        // The octant is a coarse intent envelope, not an instruction to quantize otherwise valid continuous locomotion.
        // Preserve the continuous direction while it remains within the declared 45-degree sector. If it contradicts the
        // coarse intent, snap to the sector center rather than honoring a noisy or malformed fine-grained vector.
        direction = Vector2.Dot(continuous, bearingDirection) >= OctantHalfWidthCosine
            ? continuous
            : bearingDirection;
        mode = direction == continuous ? "continuous+octant" : "octant-snap";
        return true;
    }

    private static Vector2 BearingToOwnerRelativeSteeringDirection(BodyRelativeBearing bearing)
    {
        const float diagonal = 0.70710677f;
        return bearing switch
        {
            BodyRelativeBearing.Forward => new Vector2(1, 0),
            BodyRelativeBearing.ForwardLeft => new Vector2(diagonal, diagonal),
            BodyRelativeBearing.Left => new Vector2(0, 1),
            BodyRelativeBearing.BackLeft => new Vector2(-diagonal, diagonal),
            BodyRelativeBearing.Back => new Vector2(-1, 0),
            BodyRelativeBearing.BackRight => new Vector2(-diagonal, -diagonal),
            BodyRelativeBearing.Right => new Vector2(0, -1),
            BodyRelativeBearing.ForwardRight => new Vector2(diagonal, -diagonal),
            _ => Vector2.Zero,
        };
    }

    private static Vector2 OwnerRelativeToParentSteeringDirection(
        Vector2 ownerRelative,
        Angle localRotation)
    {
        var cos = (float)Math.Cos(localRotation.Theta);
        var sin = (float)Math.Sin(localRotation.Theta);
        return new Vector2(
            ownerRelative.X * cos - ownerRelative.Y * sin,
            ownerRelative.X * sin + ownerRelative.Y * cos);
    }

    private sealed class ActiveDirectionalSteering
    {
        internal required ActionProposalId ProposalId { get; init; }
        internal required BodyId BodyId { get; init; }
        internal required EntityUid Entity { get; init; }
        internal required EntityUid ParentUid { get; init; }
        internal required Vector2 StartPosition { get; init; }
        internal required Vector2 ParentDirection { get; init; }
        internal required float ProgressHorizon { get; init; }
        internal float BestForwardProgress { get; set; }
        internal float LastObservedBestForwardProgress { get; set; }
        internal ulong LastProgressCheckTick { get; set; }
        internal int ConsecutiveStallChecks { get; set; }
    }
}