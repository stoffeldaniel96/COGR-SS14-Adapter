using System.Linq;
using System.Numerics;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Time;
using Content.Server.COGR.Systems;
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
    private const float ProjectedObjectiveMaximumMinimumProgress = 0.01f;
    private const float ProjectedObjectiveMinimumProgressFraction = 0.05f;

    private readonly Dictionary<ActionProposalId, ActiveProjectedObjectiveSteering> _projectedObjectiveSteering = new();

    private ActionExecutionResult StartProjectedObjectiveSteering(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<SteerToBodyRelativePointActionParams>(attempt.Parameters);
        if (parameters is null)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Invalid projected body-relative steering objective");
        }

        if (!TryResolvePlanarObjectiveNativeOffset(
                parameters.ObjectiveOffset,
                out _,
                out var failureDetail))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                failureDetail ?? "Invalid projected body-relative steering objective");
        }

        var entity = ResolveSteeringBody(attempt.BodyId);
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

        if (!COGREmbodimentSpatialProjection.TryOwnerRelativeLocalToParentCoordinates(
                xform.ParentUid,
                xform.LocalPosition,
                xform.LocalRotation,
                parameters.ObjectiveOffset.Forward,
                parameters.ObjectiveOffset.Left,
                out var targetCoordinates,
                out var ownerRelativeNativeOffset,
                out var parentOffset)
            || parentOffset == Vector2.Zero)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetLocationInvalidated,
                "Projected body-relative objective has no finite planar Station realization");
        }

        var targetPosition = targetCoordinates.Position;
        var directDistance = parentOffset.Length();
        var minimumProgress = MathF.Min(
            ProjectedObjectiveMaximumMinimumProgress,
            directDistance * ProjectedObjectiveMinimumProgressFraction);

        EnsureComp<InputMoverComponent>(entity.Value);
        EnsureComp<MovementSpeedModifierComponent>(entity.Value);
        EnsureComp<MobMoverComponent>(entity.Value);
        EnsureComp<ActiveNPCComponent>(entity.Value);

        // Resolve the cognition-authored egocentric point to one native parent-local coordinate exactly once. NPC steering may
        // choose ordinary local avoidance/path geometry on the way to this coordinate, but Station receives no referent that
        // could be followed and never refreshes the endpoint from later perception.
        _npcSteering.Unregister(entity.Value);
        var steering = _npcSteering.Register(entity.Value, targetCoordinates);
        if (!TryResolveProjectedObjectiveArrivalTolerance(steering.Range, out var arrivalTolerance))
        {
            _npcSteering.Unregister(entity.Value);
            RemComp<ActiveNPCComponent>(entity.Value);
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Native steering exposes no finite positive arrival range for projected objective completion");
        }

        // Feed the exact live native control resolution back through the bounded normalized embodiment evidence channel.
        // Cognition may use that evidence prospectively on later attempts, but Station remains authoritative over this attempt.
        if (EntityManager.TrySystem<COGRLocomotorRealizabilitySystem>(out var realizabilitySystem))
            realizabilitySystem.ObserveProjectedSteeringRange(attempt, steering.Range);

        // Do not ask native NPC steering to realize a displacement it already classifies as arrived. This is an
        // authoritative successful no-op at the embodiment boundary, not silent vector clamping or target mutation.
        if (IsProjectedObjectiveAlreadyWithinArrivalTolerance(directDistance, arrivalTolerance))
        {
            _npcSteering.Unregister(entity.Value);
            RemComp<ActiveNPCComponent>(entity.Value);
            if (COGRAdapterTrace.Enabled)
            {
                _sawmill.Debug(
                    "COGR projected objective already within native arrival resolution: proposal={0} directDistance={1:F3} arrivalRange={2:F3}",
                    attempt.ProposalId,
                    directDistance,
                    arrivalTolerance);
            }

            return ActionExecutionResult.Completed(null);
        }

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
            ArrivalTolerance = arrivalTolerance,
            MinimumProgressPerCheck = minimumProgress,
            MaximumTravelDistance = COGRSpatialPolicy.GetMaximumLocalTravelDistance(directDistance),
            StartTick = startTick,
            LastProgressCheckTick = startTick,
        };

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Debug(
                "COGR projected objective: proposal={0} agent={1} bodyOffset=({2:F3},{3:F3},{4:F3}) ownerNative=({5:F3},{6:F3}) parentOffset=({7:F3},{8:F3}) directDistance={9:F3} arrivalRange={10:F3} runRequested={11}",
                attempt.ProposalId,
                attempt.AgentId,
                parameters.ObjectiveOffset.Forward,
                parameters.ObjectiveOffset.Left,
                parameters.ObjectiveOffset.Up,
                ownerRelativeNativeOffset.X,
                ownerRelativeNativeOffset.Y,
                parentOffset.X,
                parentOffset.Y,
                directDistance,
                arrivalTolerance,
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

        var currentBody = ResolveSteeringBody(active.BodyId);
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
                detail: "Fixed projected body-relative objective reached within native steering arrival range; cognition should reassess current evidence");
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

    private static CapabilityValidationResult ValidateProjectedObjectiveSteeringParams(ReadOnlyMemory<byte> parameters)
    {
        var parsed = ActionParameterSerializer.Deserialize<SteerToBodyRelativePointActionParams>(parameters);
        if (parsed is null)
        {
            return CapabilityValidationResult.Invalid(
                ActionRejectionReason.InvalidParameters,
                "Invalid projected body-relative steering objective");
        }

        if (!TryResolvePlanarObjectiveNativeOffset(parsed.ObjectiveOffset, out _, out var detail))
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

        if (!COGREmbodimentSpatialProjection.TryOwnerRelativeLocalToNative(
                objectiveOffset.Forward,
                objectiveOffset.Left,
                out ownerRelativeNativeOffset))
        {
            failureDetail = "Projected body-relative steering objective has no finite valid embodiment calibration";
            return false;
        }

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

    private static bool TryResolveProjectedObjectiveArrivalTolerance(
        float nativeSteeringRange,
        out float arrivalTolerance)
    {
        arrivalTolerance = nativeSteeringRange;
        return float.IsFinite(nativeSteeringRange) && nativeSteeringRange > 0f;
    }

    private static bool IsProjectedObjectiveAlreadyWithinArrivalTolerance(
        float directDistance,
        float arrivalTolerance) =>
        float.IsFinite(directDistance)
        && float.IsFinite(arrivalTolerance)
        && directDistance > 0f
        && arrivalTolerance > 0f
        && directDistance <= arrivalTolerance;

    private sealed class ActiveProjectedObjectiveSteering
    {
        internal required ActionProposalId ProposalId { get; init; }
        internal required BodyId BodyId { get; init; }
        internal required EntityUid Entity { get; init; }
        internal required EntityUid ParentUid { get; init; }
        internal required Vector2 TargetPosition { get; init; }
        internal required Vector2 LastSampledPosition { get; set; }
        internal required Vector2 LastProgressPosition { get; set; }
        internal required float ArrivalTolerance { get; init; }
        internal required float MinimumProgressPerCheck { get; init; }
        internal required float MaximumTravelDistance { get; init; }
        internal required ulong StartTick { get; init; }
        internal required ulong LastProgressCheckTick { get; set; }
        internal float DistanceTraveled { get; set; }
        internal int ConsecutiveStallChecks { get; set; }
    }
}
