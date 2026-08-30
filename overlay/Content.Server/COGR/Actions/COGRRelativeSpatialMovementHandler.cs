using System.Linq;
using System.Numerics;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using EnvironmentReferenceId = COGR.Core.Identifiers.EnvironmentRef;
using COGR.Core.Time;
using Content.Server.COGR;
using Content.Shared.Buckle.Components;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Map;

namespace Content.Server.COGR.Actions;

/// <summary>
/// Executes actor-relative movement used by cognition to establish a spatial relation.
/// Station owns native pathfinding realization, but its local pathfinding horizon is a per-leg execution budget,
/// not a semantic limit on how far a Coggent may intend to approach a known target.
/// </summary>
public sealed class COGRRelativeSpatialMovementHandler
{
    private const int StallCheckIntervalTicks = 30;
    private const float MinimumProgressPerCheck = 0.05f;
    private const int MaximumConsecutiveStalls = 6;
    private const float LegArrivalTolerance = 0.65f;
    private const float TargetReplanDistance = 0.5f;

    private readonly Dictionary<ActionProposalId, ActiveRelativeSpatialMovement> _activeMovements = new();

    /// <summary>
    /// Starts locomotion toward one already-scoped opaque environment reference.
    /// V1 supports only the generic within-reach relation. Distant targets are approached through bounded local legs.
    /// An explicitly maintained relation remains active while the target stays currently observed; satisfaction pauses
    /// locomotion rather than terminating the action.
    /// </summary>
    public ActionExecutionResult Start(
        ActionAttempt attempt,
        IEntityManager entityManager,
        EnvironmentReferenceId targetRef,
        Func<EnvironmentReferenceId, EntityUid?> resolveReference,
        Func<EnvironmentReferenceId, bool>? isCurrentlyObserved = null,
        Func<bool>? hasCurrentAuthority = null)
    {
        var parameters = ActionParameterSerializer.Deserialize<EstablishSpatialRelationParams>(attempt.Parameters);
        if (parameters is null)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid relative-spatial movement parameters");
        if (parameters.Relation != RelativeSpatialRelation.WithinReach)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Unsupported relative spatial relation");
        if (parameters.Maintain
            && (isCurrentlyObserved is null || hasCurrentAuthority is null))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Maintained relative-spatial movement requires current perception and authority validators");
        }
        if (parameters.Maintain && !isCurrentlyObserved!(targetRef))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetMovedOutOfRange,
                "Maintained relation target is not present in the current actor-relative semantic replica");
        }

        var actor = GetEntityForBody(attempt.BodyId, entityManager);
        if (actor is null)
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");

        var incapCheck = CheckIncapacitation(actor.Value, entityManager);
        if (!incapCheck.CanAct)
            return ActionExecutionResult.Failed(ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);

        var target = resolveReference(targetRef);
        if (target is null || !entityManager.EntityExists(target.Value))
            return ActionExecutionResult.Failed(ActionFailureReason.TargetRemoved, "Target reference could not be resolved");

        if (!entityManager.TryGetComponent<TransformComponent>(actor.Value, out var actorXform)
            || !entityManager.TryGetComponent<TransformComponent>(target.Value, out var targetXform))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetLocationInvalidated, "Actor or target has no transform");
        }

        if (!ShareLocalCoordinateSpace(actorXform, targetXform))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetLocationInvalidated,
                "Target is outside the adapter-supported movement coordinate space");
        }

        if (!entityManager.TrySystem<SharedInteractionSystem>(out var interactionSystem))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "InteractionSystem not available");
        if (!entityManager.TrySystem<SharedTransformSystem>(out var transformSystem))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "TransformSystem not available");
        if (!entityManager.TrySystem<Content.Server.NPC.Systems.NPCSteeringSystem>(out var steeringSystem))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "NPCSteeringSystem not available");

        var actorMap = transformSystem.GetMapCoordinates(actor.Value, actorXform);
        var targetMap = transformSystem.GetMapCoordinates(target.Value, targetXform);
        if (actorMap.MapId != targetMap.MapId)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetLocationInvalidated,
                "Actor and target are not on the same map");
        }

        var relationSatisfied = interactionSystem.InRangeUnobstructed(
            actor.Value,
            target.Value,
            SharedInteractionSystem.InteractionRange);
        if (relationSatisfied && !parameters.Maintain)
        {
            ApplyTerminalOrientation(
                parameters,
                actor.Value,
                target.Value,
                actorXform,
                targetXform,
                entityManager);
            return ActionExecutionResult.Completed(null);
        }

        var movement = new ActiveRelativeSpatialMovement
        {
            ProposalId = attempt.ProposalId,
            BodyId = attempt.BodyId,
            Actor = actor.Value,
            Target = target.Value,
            TargetRef = targetRef,
            Parameters = parameters,
            IsCurrentlyObserved = isCurrentlyObserved,
            HasCurrentAuthority = hasCurrentAuthority,
            RelationSatisfied = relationSatisfied,
            LegStartTick = attempt.ProposedAtTick.Value,
            LastSampledActorPosition = actorMap.Position,
            LastProgressActorPosition = actorMap.Position,
            LastTargetPosition = targetMap.Position,
            CurrentLegGoalPosition = actorMap.Position,
            LastProgressCheckTick = attempt.ProposedAtTick.Value,
            MaximumLegTravelDistance = 0f,
        };

        if (relationSatisfied)
        {
            StopEntityMovement(actor.Value, entityManager);
            ApplyTerminalOrientation(
                parameters,
                actor.Value,
                target.Value,
                actorXform,
                targetXform,
                entityManager);
            _activeMovements[attempt.ProposalId] = movement;
            return ActionExecutionResult.Started();
        }

        EnsureMovementComponents(actor.Value, entityManager);
        if (!TryStartNextLeg(
                movement,
                attempt.ProposedAtTick.Value,
                actorXform,
                targetXform,
                actorMap,
                targetMap,
                entityManager,
                transformSystem,
                steeringSystem))
        {
            StopEntityMovement(actor.Value, entityManager);
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetLocationInvalidated,
                "Could not construct a valid local approach leg");
        }

        _activeMovements[attempt.ProposalId] = movement;
        return ActionExecutionResult.Started();
    }

    /// <summary>Ticks active relation movements and returns terminal authoritative results.</summary>
    public IReadOnlyList<ActionResult> Tick(
        ulong currentTick,
        IEntityManager entityManager,
        IActiveActionRegistry registry)
    {
        var results = new List<ActionResult>();
        var completed = new List<ActionProposalId>();

        foreach (var (proposalId, movement) in _activeMovements)
        {
            var result = Progress(proposalId, movement, currentTick, entityManager, registry);
            if (result is null)
                continue;

            completed.Add(proposalId);
            results.Add(result);
        }

        foreach (var proposalId in completed)
            _activeMovements.Remove(proposalId);

        return results;
    }

    /// <summary>Stops and forgets one relative movement if it is currently tracked.</summary>
    public void CleanupMovement(ActionProposalId proposalId, IEntityManager entityManager)
    {
        if (_activeMovements.Remove(proposalId, out var movement))
            StopEntityMovement(movement.Actor, entityManager);
    }

    /// <summary>Stops and forgets every relative movement currently owned by one body.</summary>
    public void CleanupAllForBody(BodyId bodyId, IEntityManager entityManager)
    {
        var proposalIds = _activeMovements
            .Where(pair => pair.Value.BodyId == bodyId)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (var proposalId in proposalIds)
            CleanupMovement(proposalId, entityManager);
    }

    private static ActionResult? Progress(
        ActionProposalId proposalId,
        ActiveRelativeSpatialMovement movement,
        ulong currentTick,
        IEntityManager entityManager,
        IActiveActionRegistry registry)
    {
        var attempt = registry.GetAction(proposalId);
        if (attempt is null)
        {
            StopEntityMovement(movement.Actor, entityManager);
            return ActionResult.Cancelled(proposalId, new SimTick(currentTick), "Action no longer active in Station registry");
        }

        if (movement.Parameters.Maintain)
        {
            if (movement.HasCurrentAuthority?.Invoke() != true)
            {
                StopEntityMovement(movement.Actor, entityManager);
                registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
                return ActionResult.Failed(
                    proposalId,
                    new SimTick(currentTick),
                    ActionFailureReason.BodyAuthorityRevoked,
                    "Body authority changed while maintained spatial relation was active");
            }

            if (movement.IsCurrentlyObserved?.Invoke(movement.TargetRef) != true)
            {
                StopEntityMovement(movement.Actor, entityManager);
                registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
                return ActionResult.Failed(
                    proposalId,
                    new SimTick(currentTick),
                    ActionFailureReason.TargetMovedOutOfRange,
                    "Maintained relation target left the current actor-relative semantic replica");
            }
        }

        if (!entityManager.EntityExists(movement.Actor))
        {
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(proposalId, new SimTick(currentTick), ActionFailureReason.BodyDied, "Body entity deleted");
        }

        if (GetEntityForBody(movement.BodyId, entityManager) != movement.Actor)
        {
            StopEntityMovement(movement.Actor, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(proposalId, new SimTick(currentTick), ActionFailureReason.BodyReplaced, "Body replaced");
        }

        if (!entityManager.EntityExists(movement.Target))
        {
            StopEntityMovement(movement.Actor, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(proposalId, new SimTick(currentTick), ActionFailureReason.TargetRemoved, "Target entity removed");
        }

        var incapCheck = CheckIncapacitation(movement.Actor, entityManager);
        if (!incapCheck.CanAct)
        {
            StopEntityMovement(movement.Actor, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(
                proposalId,
                new SimTick(currentTick),
                ActionFailureReason.BodyBecameIncapacitated,
                incapCheck.Reason);
        }

        if (!entityManager.TryGetComponent<TransformComponent>(movement.Actor, out var actorXform)
            || !entityManager.TryGetComponent<TransformComponent>(movement.Target, out var targetXform)
            || !ShareLocalCoordinateSpace(actorXform, targetXform))
        {
            StopEntityMovement(movement.Actor, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(
                proposalId,
                new SimTick(currentTick),
                ActionFailureReason.TargetLocationInvalidated,
                "Actor and target no longer share the adapter-supported movement coordinate space");
        }

        if (!entityManager.TrySystem<SharedInteractionSystem>(out var interactionSystem)
            || !entityManager.TrySystem<SharedTransformSystem>(out var transformSystem)
            || !entityManager.TrySystem<Content.Server.NPC.Systems.NPCSteeringSystem>(out var steeringSystem))
        {
            StopEntityMovement(movement.Actor, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(
                proposalId,
                new SimTick(currentTick),
                ActionFailureReason.Unspecified,
                "Native movement systems are unavailable");
        }

        if (interactionSystem.InRangeUnobstructed(
                movement.Actor,
                movement.Target,
                SharedInteractionSystem.InteractionRange))
        {
            if (movement.Parameters.Maintain)
            {
                if (!movement.RelationSatisfied)
                {
                    StopEntityMovement(movement.Actor, entityManager);
                    ApplyTerminalOrientation(
                        movement.Parameters,
                        movement.Actor,
                        movement.Target,
                        actorXform,
                        targetXform,
                        entityManager);
                    movement.RelationSatisfied = true;
                }

                if (attempt.State == ActionState.Started)
                    registry.UpdateState(proposalId, ActionState.Progressing, new SimTick(currentTick));
                return null;
            }

            StopEntityMovement(movement.Actor, entityManager);
            ApplyTerminalOrientation(
                movement.Parameters,
                movement.Actor,
                movement.Target,
                actorXform,
                targetXform,
                entityManager);
            registry.UpdateState(proposalId, ActionState.Completed, new SimTick(currentTick));
            return ActionResult.Completed(
                proposalId,
                new SimTick(currentTick),
                detail: "Requested actor-relative spatial relation established");
        }

        var actorMap = transformSystem.GetMapCoordinates(movement.Actor, actorXform);
        var targetMap = transformSystem.GetMapCoordinates(movement.Target, targetXform);
        if (actorMap.MapId != targetMap.MapId)
        {
            StopEntityMovement(movement.Actor, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(
                proposalId,
                new SimTick(currentTick),
                ActionFailureReason.TargetLocationInvalidated,
                "Actor and target moved onto different maps");
        }

        if (movement.RelationSatisfied)
        {
            movement.RelationSatisfied = false;
            EnsureMovementComponents(movement.Actor, entityManager);
            if (!TryStartNextLeg(
                    movement,
                    currentTick,
                    actorXform,
                    targetXform,
                    actorMap,
                    targetMap,
                    entityManager,
                    transformSystem,
                    steeringSystem))
            {
                StopEntityMovement(movement.Actor, entityManager);
                registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
                return ActionResult.Failed(
                    proposalId,
                    new SimTick(currentTick),
                    ActionFailureReason.TargetLocationInvalidated,
                    "Could not resume native movement after maintained relation was lost");
            }

            if (attempt.State == ActionState.Started)
                registry.UpdateState(proposalId, ActionState.Progressing, new SimTick(currentTick));
            return null;
        }

        var legTicksElapsed = currentTick - movement.LegStartTick;
        if (legTicksElapsed > COGRSpatialPolicy.MaximumLocalMovementTicks)
        {
            StopEntityMovement(movement.Actor, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(
                proposalId,
                new SimTick(currentTick),
                ActionFailureReason.NoPathFound,
                "Current bounded local pathfinding leg timed out without enough progress");
        }

        movement.LegDistanceTraveled += (actorMap.Position - movement.LastSampledActorPosition).Length();
        movement.LastSampledActorPosition = actorMap.Position;
        if (movement.LegDistanceTraveled > movement.MaximumLegTravelDistance)
        {
            StopEntityMovement(movement.Actor, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(
                proposalId,
                new SimTick(currentTick),
                ActionFailureReason.PathBecameBlocked,
                "Current local pathfinding leg exceeded its bounded detour budget");
        }

        var ticksSinceProgressCheck = currentTick - movement.LastProgressCheckTick;
        if (ticksSinceProgressCheck >= StallCheckIntervalTicks)
        {
            var progress = (actorMap.Position - movement.LastProgressActorPosition).Length();
            if (progress < MinimumProgressPerCheck)
            {
                movement.ConsecutiveStallCount++;
                if (movement.ConsecutiveStallCount >= MaximumConsecutiveStalls)
                {
                    StopEntityMovement(movement.Actor, entityManager);
                    registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
                    return ActionResult.Failed(
                        proposalId,
                        new SimTick(currentTick),
                        ActionFailureReason.NoPathFound,
                        "Native local pathfinding persistently stalled");
                }
            }
            else
            {
                movement.ConsecutiveStallCount = 0;
            }

            movement.LastProgressCheckTick = currentTick;
            movement.LastProgressActorPosition = actorMap.Position;
        }

        var legReached = (actorMap.Position - movement.CurrentLegGoalPosition).LengthSquared()
            <= LegArrivalTolerance * LegArrivalTolerance;
        var targetMoved = (targetMap.Position - movement.LastTargetPosition).LengthSquared()
            >= TargetReplanDistance * TargetReplanDistance;

        if (legReached || targetMoved)
        {
            if (!TryStartNextLeg(
                    movement,
                    currentTick,
                    actorXform,
                    targetXform,
                    actorMap,
                    targetMap,
                    entityManager,
                    transformSystem,
                    steeringSystem))
            {
                StopEntityMovement(movement.Actor, entityManager);
                registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
                return ActionResult.Failed(
                    proposalId,
                    new SimTick(currentTick),
                    ActionFailureReason.TargetLocationInvalidated,
                    "Could not construct the next bounded local approach leg");
            }
        }

        var currentAttempt = registry.GetAction(proposalId);
        if (currentAttempt?.State == ActionState.Started)
            registry.UpdateState(proposalId, ActionState.Progressing, new SimTick(currentTick));

        return null;
    }

    private static bool TryStartNextLeg(
        ActiveRelativeSpatialMovement movement,
        ulong currentTick,
        TransformComponent actorXform,
        TransformComponent targetXform,
        MapCoordinates actorMap,
        MapCoordinates targetMap,
        IEntityManager entityManager,
        SharedTransformSystem transformSystem,
        Content.Server.NPC.Systems.NPCSteeringSystem steeringSystem)
    {
        if (!TryCreateApproachCoordinates(
                actorXform,
                targetXform,
                actorMap,
                targetMap,
                entityManager,
                transformSystem,
                out var coordinates,
                out var goalMapPosition,
                out var directLegDistance))
        {
            return false;
        }

        steeringSystem.Register(movement.Actor, coordinates);

        movement.LegStartTick = currentTick;
        movement.CurrentLegGoalPosition = goalMapPosition;
        movement.LastSampledActorPosition = actorMap.Position;
        movement.LastProgressActorPosition = actorMap.Position;
        movement.LastTargetPosition = targetMap.Position;
        movement.LastProgressCheckTick = currentTick;
        movement.LegDistanceTraveled = 0f;
        movement.MaximumLegTravelDistance = COGRSpatialPolicy.GetMaximumLocalTravelDistance(directLegDistance);
        movement.ConsecutiveStallCount = 0;
        return true;
    }

    private static bool TryCreateApproachCoordinates(
        TransformComponent actorXform,
        TransformComponent targetXform,
        MapCoordinates actorMap,
        MapCoordinates targetMap,
        IEntityManager entityManager,
        SharedTransformSystem transformSystem,
        out EntityCoordinates coordinates,
        out Vector2 goalMapPosition,
        out float directLegDistance)
    {
        coordinates = EntityCoordinates.Invalid;
        goalMapPosition = actorMap.Position;
        directLegDistance = 0f;

        if (actorMap.MapId != targetMap.MapId || !ShareLocalCoordinateSpace(actorXform, targetXform))
            return false;

        var offset = targetMap.Position - actorMap.Position;
        var centerDistance = offset.Length();
        if (!float.IsFinite(centerDistance))
            return false;

        if (centerDistance <= 0.0001f)
        {
            directLegDistance = 0f;
            goalMapPosition = actorMap.Position;
        }
        else
        {
            var direction = offset / centerDistance;
            var desiredCenterDistance = SharedInteractionSystem.InteractionRange * 0.75f;
            var remainingDirectTravel = centerDistance > desiredCenterDistance
                ? centerDistance - desiredCenterDistance
                : centerDistance;

            directLegDistance = COGRSpatialPolicy.GetLocalPathfindingAdvanceDistance(remainingDirectTravel);
            goalMapPosition = actorMap.Position + direction * directLegDistance;
        }

        var coordinateSpace = actorXform.GridUid ?? actorXform.MapUid;
        if (!coordinateSpace.HasValue
            || !entityManager.TryGetComponent<TransformComponent>(coordinateSpace.Value, out var coordinateXform))
        {
            return false;
        }

        coordinates = transformSystem.ToCoordinates(
            (coordinateSpace.Value, coordinateXform),
            new MapCoordinates(goalMapPosition, actorMap.MapId));
        return transformSystem.IsValid(coordinates);
    }

    private static void ApplyTerminalOrientation(
        EstablishSpatialRelationParams parameters,
        EntityUid actor,
        EntityUid target,
        TransformComponent actorXform,
        TransformComponent targetXform,
        IEntityManager entityManager)
    {
        if (parameters.TerminalOrientation != TerminalOrientationPreference.FaceTarget)
            return;
        if (!ShareLocalCoordinateSpace(actorXform, targetXform))
            return;
        if (!entityManager.TrySystem<SharedTransformSystem>(out var transformSystem)
            || !entityManager.TrySystem<RotateToFaceSystem>(out var rotateSystem))
        {
            return;
        }

        var targetMapCoordinates = transformSystem.GetMapCoordinates(target, targetXform);
        rotateSystem.TryFaceCoordinates(actor, targetMapCoordinates.Position, actorXform);
    }

    private static bool ShareLocalCoordinateSpace(TransformComponent actor, TransformComponent target)
    {
        var actorSpace = actor.GridUid ?? actor.MapUid;
        var targetSpace = target.GridUid ?? target.MapUid;
        return actorSpace != null && actorSpace == targetSpace;
    }

    private static EntityUid? GetEntityForBody(BodyId bodyId, IEntityManager entityManager)
    {
        var query = entityManager.AllEntityQueryEnumerator<Content.Shared.COGR.Components.COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (component.BodyId == bodyId.ToGuid())
                return uid;
        }

        return null;
    }

    private static IncapacitationCheck CheckIncapacitation(EntityUid entity, IEntityManager entityManager)
    {
        if (entityManager.TryGetComponent<MobStateComponent>(entity, out var mobState))
        {
            if (mobState.CurrentState == MobState.Dead)
                return new IncapacitationCheck(false, "Body is dead");
            if (mobState.CurrentState == MobState.Critical)
                return new IncapacitationCheck(false, "Body is in critical condition");
        }

        if (entityManager.TryGetComponent<BuckleComponent>(entity, out var buckle) && buckle.Buckled)
            return new IncapacitationCheck(false, "Body is buckled/restrained");

        return new IncapacitationCheck(true, null);
    }

    private static void EnsureMovementComponents(EntityUid entity, IEntityManager entityManager)
    {
        entityManager.EnsureComponent<Content.Shared.Movement.Components.InputMoverComponent>(entity);
        entityManager.EnsureComponent<Content.Shared.Movement.Components.MovementSpeedModifierComponent>(entity);
        entityManager.EnsureComponent<Content.Shared.Movement.Components.MobMoverComponent>(entity);
        entityManager.EnsureComponent<Content.Shared.NPC.ActiveNPCComponent>(entity);
    }

    private static void StopEntityMovement(EntityUid entity, IEntityManager entityManager)
    {
        if (entityManager.TrySystem<Content.Server.NPC.Systems.NPCSteeringSystem>(out var steeringSystem))
            steeringSystem.Unregister(entity);

        entityManager.RemoveComponent<Content.Shared.NPC.ActiveNPCComponent>(entity);
        if (entityManager.TryGetComponent<Content.Shared.Movement.Components.InputMoverComponent>(entity, out var mover))
        {
            mover.CurTickSprintMovement = Vector2.Zero;
            mover.CurTickWalkMovement = Vector2.Zero;
            entityManager.Dirty(entity, mover);
        }
    }

    private sealed class ActiveRelativeSpatialMovement
    {
        public required ActionProposalId ProposalId { get; init; }
        public required BodyId BodyId { get; init; }
        public required EntityUid Actor { get; init; }
        public required EntityUid Target { get; init; }
        public required EnvironmentReferenceId TargetRef { get; init; }
        public required EstablishSpatialRelationParams Parameters { get; init; }
        public Func<EnvironmentReferenceId, bool>? IsCurrentlyObserved { get; init; }
        public Func<bool>? HasCurrentAuthority { get; init; }
        public bool RelationSatisfied { get; set; }
        public required ulong LegStartTick { get; set; }
        public required Vector2 LastSampledActorPosition { get; set; }
        public required Vector2 LastProgressActorPosition { get; set; }
        public required Vector2 LastTargetPosition { get; set; }
        public required Vector2 CurrentLegGoalPosition { get; set; }
        public required ulong LastProgressCheckTick { get; set; }
        public required float MaximumLegTravelDistance { get; set; }
        public float LegDistanceTraveled { get; set; }
        public int ConsecutiveStallCount { get; set; }
    }
}