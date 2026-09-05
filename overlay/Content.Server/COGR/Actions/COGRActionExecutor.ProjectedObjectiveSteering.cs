using System.Numerics;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Time;
using Content.Server.NPC.Components;
using Content.Shared.COGR.Components;
using Content.Shared.Movement.Components;
using Content.Shared.NPC;
using Robust.Shared.Map;

namespace Content.Server.COGR.Actions;

/// <summary>
/// Realizes one cognition-authored body-relative endpoint as a fixed Station-native local steering objective.
/// The endpoint is resolved exactly once from the authoritative body pose and embodiment calibration at action start.
/// No target identity, route cursor, or permission to refresh/chase the endpoint is retained by the adapter.
/// </summary>
public sealed partial class COGRActionExecutor
{
    private const ulong ProjectedObjectiveProgressCheckTicks = 30;
    private const int ProjectedObjectiveMaximumStallChecks = 6;
    private const float ProjectedObjectiveMaximumArrivalTolerance = 0.10f;
    private const float ProjectedObjectiveArrivalToleranceFraction = 0.20f;
    private const float ProjectedObjectiveMaximumMinimumProgress = 0.01f;
    private const float ProjectedObjectiveMinimumProgressFraction = 0.05f;

    private readonly Dictionary<ActionProposalId, ActiveProjectedObjectiveSteering> _projectedObjectiveSteering = new();

    private ActionExecutionResult StartProjectedObjectiveSteering(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<SteerToBodyRelativePointActionParams>(attempt.Parameters);
        if (parameters is null
            || !TryResolvePlanarObjectiveNativeOffset(parameters.ObjectiveOffset, out var ownerRelativeNativeOffset, out var failureDetail))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                failureDetail ?? "Invalid projected body-relative steering objective");
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

        if (xform.ParentUid == EntityUid.Invalid)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetLocationInvalidated,
                "Body has no valid local spatial reference frame");
        }

        var parentOffset = OwnerRelativeObjectiveToParentOffset(ownerRelativeNativeOffset, xform.LocalRotation);
        if (!float.IsFinite(parentOffset.X)
            || !float.IsFinite(parentOffset.Y)
            || parentOffset == Vector2.Zero)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetLocationInvalidated,
                "Projected body-relative objective has no finite planar Station realization");
        }

        var targetPosition = xform.LocalPosition + parentOffset;
        if (!float.IsFinite(targetPosition.X) || !float.IsFinite(targetPosition.Y))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetLocationInvalidated,
                "Projected body-relative objective overflowed the current local spatial frame");
        }

        var directDistance = parentOffset.Length();
        var arrivalTolerance = MathF.Min(
            ProjectedObjectiveMaximumArrivalTolerance,
            directDistance * ProjectedObjectiveArrivalToleranceFraction);
        var minimumProgress = MathF.Min(
            ProjectedObjectiveMaximumMinimumProgress,
            directDistance * ProjectedObjectiveMinimumProgressFraction);

        EnsureComp<InputMoverComponent>(entity.Value);
        EnsureComp<MovementSpeedModifierComponent>(entity.Value);
        EnsureComp<MobMoverComponent>(entity.Value);
        EnsureComp<ActiveNPCComponent>(entity.Value);

        // Resolve the cognition-authored egocentric point to one native coordinate exactly once. NPC steering may choose
        // ordinary local avoidance/path geometry on the way to this coordinate, but Station receives no referent that could
        // be followed and never refreshes the endpoint from later perception.
        var targetCoordinates = new EntityCoordinates(xform.ParentUid, targetPosition);
        _npcSteering.Unregister(entity.Value);
        var steering = _npcSteering.Register(entity.Value, targetCoordinates);
        steering.Status = SteeringStatus.Moving;

        var startTick = (ulong)_timing.CurTick.Value;
        _projectedObjectiveSteering[attempt.ProposalId] = new ActiveProjectedObjectiveSteering
        {
            ProposalId = attempt.ProposalId,
            BodyId = attempt.BodyId,
            Entity = entity.Value,
            ParentUid = xform.ParentUid,
            TargetPosition = targetPosition,
            LastSampledPosition = xform.LocalPosition,
            LastProgressPosition = xform.LocalPosition,
            DirectDistance = directDistance,
            ArrivalTolerance = arrivalTolerance,
            MinimumProgressPerCheck = minimumProgress,
            MaximumTravelDistance = COGRSpatialPolicy.GetMaximumLocalTravelDistance(directDistance),
            StartTick = startTick,
            LastProgressCheckTick = startTick,
        };

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Debug(
                "COGR projected objective: proposal={0} agent={1} bodyOffset=({2:F3},{3:F3},{4:F3}) nativeOffset=({5:F3},{6:F3}) directDistance={7:F3} runRequested={8}",
                attempt.ProposalId,
                attempt.AgentId,
                parameters.ObjectiveOffset.Forward,
                parameters.ObjectiveOffset.Left,
                parameters.ObjectiveOffset.Up,
                parentOffset.X,
                parentOffset.Y,
                directDistance,
                parameters.Run);
        }

        return ActionExecutionResult.Started();
    }

    private IReadOnlyList<ActionResult> TickProjectedObjectiveSteering(ulong currentTick)
    {
        if (_projectedObjectiveSteering.Count == 0)
            return Array.Empty<ActionResult>();

        var results = new List<ActionResult>();
        foreach (var active in _projectedObjectiveSteering.Values.ToArray())
        {
            var result = TickProjectedObjectiveSteering(active, currentTick);
            if (result is not null)
                results.Add(result);
        }

        return results;
    }

    private ActionResult? TickProjectedObjectiveSteering(
        ActiveProjectedObjectiveSteering active,
        ulong currentTick)
    {
        var attempt = _actionRegistry.GetAction(active.ProposalId);
        if (attempt is null)
        {
            CleanupProjectedObjectiveSteering(active.ProposalId);
            return null;
        }

        var tick = new SimTick(currentTick);
        if (!Exists(active.Entity))
        {
            CleanupProjectedObjectiveSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.BodyDied,
                "Body entity was deleted during projected objective steering");
        }

        var currentBody = ResolveDirectionalSteeringBody(active.BodyId);
        if (currentBody != active.Entity)
        {
            CleanupProjectedObjectiveSteering(active.ProposalId);
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
            CleanupProjectedObjectiveSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.TargetLocationInvalidated,
                "Projected objective spatial frame changed before the fixed endpoint was reached");
        }

        if (!TryComp<NPCSteeringComponent>(active.Entity, out var steering)
            || steering.Status == SteeringStatus.NoPath)
        {
            CleanupProjectedObjectiveSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.NoPathFound,
                "Native steering cannot reach the projected objective");
        }

        var currentPosition = xform.LocalPosition;
        var remainingDistance = (active.TargetPosition - currentPosition).Length();
        if (remainingDistance <= active.ArrivalTolerance)
        {
            CleanupProjectedObjectiveSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Completed, tick);
            return ActionResult.Completed(
                active.ProposalId,
                tick,
                detail: "Fixed projected body-relative objective reached; cognition should reassess current evidence");
        }

        var sampledTravel = (currentPosition - active.LastSampledPosition).Length();
        if (float.IsFinite(sampledTravel))
            active.DistanceTraveled += sampledTravel;
        active.LastSampledPosition = currentPosition;

        if (active.DistanceTraveled > active.MaximumTravelDistance)
        {
            CleanupProjectedObjectiveSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.PathBecameBlocked,
                "Projected objective exceeded its bounded local detour budget");
        }

        if (currentTick - active.StartTick > COGRSpatialPolicy.MaximumLocalMovementTicks)
        {
            CleanupProjectedObjectiveSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.NoPathFound,
                "Projected objective local steering timed out without reaching the fixed endpoint");
        }

        if (currentTick - active.LastProgressCheckTick >= ProjectedObjectiveProgressCheckTicks)
        {
            var moved = (currentPosition - active.LastProgressPosition).Length();
            if (!float.IsFinite(moved) || moved < active.MinimumProgressPerCheck)
            {
                active.ConsecutiveStallChecks++;
                if (active.ConsecutiveStallChecks >= ProjectedObjectiveMaximumStallChecks)
                {
                    CleanupProjectedObjectiveSteering(active.ProposalId);
                    _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
                    return ActionResult.Failed(
                        active.ProposalId,
                        tick,
                        ActionFailureReason.NoPathFound,
                        "Projected objective native steering persistently stalled");
                }
            }
            else
            {
                active.ConsecutiveStallChecks = 0;
            }

            active.LastProgressPosition = currentPosition;
            active.LastProgressCheckTick = currentTick;
        }

        if (attempt.State == ActionState.Started)
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Progressing, tick);

        return null;
    }

    private void CleanupProjectedObjectiveSteering(ActionProposalId proposalId)
    {
        if (!_projectedObjectiveSteering.Remove(proposalId, out var active))
            return;

        if (!Exists(active.Entity))
            return;

        _npcSteering.Unregister(active.Entity);
        RemComp<ActiveNPCComponent>(active.Entity);
        if (TryComp<InputMoverComponent>(active.Entity, out var mover))
        {
            mover.CurTickSprintMovement = Vector2.Zero;
            mover.CurTickWalkMovement = Vector2.Zero;
            Dirty(active.Entity, mover);
        }
    }

    private void CleanupAllProjectedObjectiveSteeringForBody(BodyId bodyId)
    {
        var proposals = _projectedObjectiveSteering
            .Where(pair => pair.Value.BodyId == bodyId)
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (var proposal in proposals)
            CleanupProjectedObjectiveSteering(proposal);
    }

    private static CapabilityValidationResult ValidateProjectedObjectiveSteeringParams(ReadOnlyMemory<byte> parameters)
    {
        var parsed = ActionParameterSerializer.Deserialize<SteerToBodyRelativePointActionParams>(parameters);
        if (parsed is null
            || !TryResolvePlanarObjectiveNativeOffset(parsed.ObjectiveOffset, out _, out var detail))
        {
            return CapabilityValidationResult.Invalid(
                ActionRejectionReason.InvalidParameters,
                detail ?? "Invalid projected body-relative steering objective");
        }

        return CapabilityValidationResult.Valid();
    }

    private static bool TryResolvePlanarObjectiveNativeOffset(
        BodyRelativePointOffset objectiveOffset,
        out Vector2 ownerRelativeNativeOffset,
        out string? failureDetail)
    {
        ownerRelativeNativeOffset = Vector2.Zero;
        failureDetail = null;

        if (!objectiveOffset.HasOffset)
        {
            failureDetail = "Projected body-relative steering objective must be finite and non-zero";
            return false;
        }

        if (!objectiveOffset.IsPlanar)
        {
            failureDetail = "SS14 ordinary locomotion cannot realize a vertical projected objective; a distinct embodiment action is required";
            return false;
        }

        double nativeForward;
        double nativeLeft;
        try
        {
            nativeForward = COGREmbodimentSpatialCalibration.LocalUnitsToNativeUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                objectiveOffset.Forward);
            nativeLeft = COGREmbodimentSpatialCalibration.LocalUnitsToNativeUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                objectiveOffset.Left);
        }
        catch (ArgumentException)
        {
            failureDetail = "Projected body-relative steering objective has no valid embodiment calibration";
            return false;
        }

        if (!double.IsFinite(nativeForward) || !double.IsFinite(nativeLeft)
            || nativeForward > float.MaxValue || nativeForward < float.MinValue
            || nativeLeft > float.MaxValue || nativeLeft < float.MinValue)
        {
            failureDetail = "Projected body-relative steering objective exceeds Station's finite planar coordinate range";
            return false;
        }

        ownerRelativeNativeOffset = new Vector2((float)nativeForward, (float)nativeLeft);
        var directDistance = ownerRelativeNativeOffset.Length();
        if (!float.IsFinite(directDistance) || directDistance <= 0f)
        {
            failureDetail = "Projected body-relative steering objective has no finite planar Station realization";
            return false;
        }

        if (directDistance > COGRSpatialPolicy.MaximumLocalPathfindingDistance)
        {
            failureDetail = $"Projected objective exceeds the {COGRSpatialPolicy.MaximumLocalPathfindingDistance:0.#}-unit bounded native pathfinding horizon";
            return false;
        }

        return true;
    }

    private static Vector2 OwnerRelativeObjectiveToParentOffset(Vector2 ownerRelativeOffset, Angle localRotation)
    {
        var cos = (float)Math.Cos(localRotation.Theta);
        var sin = (float)Math.Sin(localRotation.Theta);
        return new Vector2(
            ownerRelativeOffset.X * cos - ownerRelativeOffset.Y * sin,
            ownerRelativeOffset.X * sin + ownerRelativeOffset.Y * cos);
    }

    private sealed class ActiveProjectedObjectiveSteering
    {
        internal required ActionProposalId ProposalId { get; init; }
        internal required BodyId BodyId { get; init; }
        internal required EntityUid Entity { get; init; }
        internal required EntityUid ParentUid { get; init; }
        internal required Vector2 TargetPosition { get; init; }
        internal required Vector2 LastSampledPosition { get; set; }
        internal required Vector2 LastProgressPosition { get; set; }
        internal required float DirectDistance { get; init; }
        internal required float ArrivalTolerance { get; init; }
        internal required float MinimumProgressPerCheck { get; init; }
        internal required float MaximumTravelDistance { get; init; }
        internal required ulong StartTick { get; init; }
        internal required ulong LastProgressCheckTick { get; set; }
        internal float DistanceTraveled { get; set; }
        internal int ConsecutiveStallChecks { get; set; }
    }
}
