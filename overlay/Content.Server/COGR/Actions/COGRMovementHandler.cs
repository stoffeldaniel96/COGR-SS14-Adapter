using System.Numerics;
using System.Linq;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Time;
using Content.Server.COGR;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Buckle.Components;
using Robust.Shared.Map;

namespace Content.Server.COGR.Actions;

/// <summary>
/// F02/F03 implementation of movement action handler for COGR-controlled entities.
/// Handles turn, step, stop, and async move_to_location actions with full lifecycle.
/// </summary>
/// <remarks>
/// Per COGR-DES-005 Workstream B requirements:
/// - Handles invalid body authority (via COGRActionExecutor)
/// - Handles stale body epoch (via authority generation)
/// - Detects and reports no-path conditions
/// - Detects and reports persistent stall
/// - Checks for incapacitation (crit, dead, restrained)
/// - Handles explicit cancellation
/// - Handles connection loss (via cleanup callbacks)
/// - Detects body replacement/deletion
/// 
/// All movements use native SS14 NPC steering - no direct transform mutation.
/// </remarks>
public sealed class COGRMovementHandler
{
    private readonly Dictionary<ActionProposalId, ActiveMovement> _activeMovements = new();
    private const double DefaultSpeed = 2.0; // tiles per second
    private const double DefaultArrivalTolerance = 0.5; // tiles
    
    // Stall detection configuration
    private const int StallCheckIntervalTicks = 30; // Check every ~0.5 seconds at 60 TPS
    private const double MinProgressPerCheck = 0.05; // Must move at least 0.05 tiles per check interval
    private const int MaxConsecutiveStalls = 6; // ~3 seconds of no progress = persistent stall
    
    // Timeout configuration
    private const int MaxMovementTicks = 1800; // 30 seconds at 60 TPS

    /// <summary>
    /// Executes an immediate turn action.
    /// </summary>
    public ActionExecutionResult ExecuteTurn(ActionAttempt attempt, IEntityManager entityManager)
    {
        var parameters = ActionParameterSerializer.Deserialize<TurnActionParams>(attempt.Parameters);
        if (parameters == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid turn parameters");
        }

        var entity = GetEntityForBody(attempt.BodyId, entityManager);
        if (entity == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");
        }

        // Check incapacitation before action
        var incapCheck = CheckIncapacitation(entity.Value, entityManager);
        if (!incapCheck.CanAct)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);
        }

        // Convert Direction enum to SS14 Angle
        var angle = DirectionToAngle(parameters.TargetDirection);

        // Use RotateToFaceSystem to properly handle rotation
        if (!entityManager.TrySystem<Content.Shared.Interaction.RotateToFaceSystem>(out var rotateSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "RotateToFaceSystem not available");
        }

        // TryFaceAngle respects game rules like 4-directional sprites, buckled entities, etc.
        if (!rotateSystem.TryFaceAngle(entity.Value, angle))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.InteractionBlocked, "Unable to rotate (blocked or buckled)");
        }

        // Immediate completion
        return ActionExecutionResult.Completed(new TurnResultData
        {
            FinalDirection = parameters.TargetDirection.ToString()
        });
    }

    /// <summary>
    /// Executes a step action as an async movement using NPC steering.
    /// </summary>
    public ActionExecutionResult ExecuteStep(ActionAttempt attempt, IEntityManager entityManager)
    {
        var parameters = ActionParameterSerializer.Deserialize<StepActionParams>(attempt.Parameters);
        if (parameters == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid step parameters");
        }

        var hasWorldDirection = Enum.IsDefined(parameters.Direction)
            && parameters.Direction != global::COGR.Core.Actions.Parameters.Direction.None;
        var hasBodyRelativeDirection = Enum.IsDefined(parameters.BodyRelativeDirection)
            && parameters.BodyRelativeDirection != BodyRelativeDirection.Unknown;
        if (hasWorldDirection == hasBodyRelativeDirection)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "A step must specify exactly one world/cardinal or body-relative direction");
        }

        if (!double.IsFinite(parameters.Distance)
            || parameters.Distance <= 0
            || parameters.Distance > COGRSpatialPolicy.MaximumStepDistance)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                $"Step distance must be greater than zero and no more than {COGRSpatialPolicy.MaximumStepDistance:0.#} tiles");
        }

        var entity = GetEntityForBody(attempt.BodyId, entityManager);
        if (entity == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");
        }

        // Check incapacitation
        var incapCheck = CheckIncapacitation(entity.Value, entityManager);
        if (!incapCheck.CanAct)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);
        }

        if (!entityManager.TryGetComponent<TransformComponent>(entity.Value, out var xform))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "No transform component");
        }

        // World/cardinal steps remain available for explicit orientation knowledge. Body-relative steps are resolved only
        // here, at the embodiment boundary, through the body's authoritative current orientation.
        var currentPos = xform.LocalPosition;
        var offset = hasBodyRelativeDirection
            ? BodyRelativeDirectionToOffset(parameters.BodyRelativeDirection, xform.LocalRotation)
            : DirectionToOffset(parameters.Direction);
        var targetPos = currentPos + offset * (float) parameters.Distance;

        // Use NPC steering for proper movement
        if (!entityManager.TrySystem<Content.Server.NPC.Systems.NPCSteeringSystem>(out var steeringSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "NPCSteeringSystem not available");
        }

        // Ensure required components for NPC steering
        EnsureMovementComponents(entity.Value, entityManager);
        
        // Register with steering system to move to target
        var targetCoords = new EntityCoordinates(xform.GridUid ?? xform.MapUid ?? EntityUid.Invalid, targetPos);
        steeringSystem.Register(entity.Value, targetCoords);
        
        var logger = Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.Log.ILogManager>().GetSawmill("cogr.movement");
        logger.Info("Step: entity {0} moving to {1}", entity.Value, targetPos);

        // Track as async movement
        _activeMovements[attempt.ProposalId] = new ActiveMovement
        {
            ProposalId = attempt.ProposalId,
            BodyId = attempt.BodyId,
            AgentId = attempt.AgentId,
            Entity = entity.Value,
            TargetLocation = targetPos,
            ArrivalTolerance = 0.15, // Tighter tolerance for step
            Speed = 4.0,
            DistanceTraveled = 0.0,
            StartTick = attempt.ProposedAtTick.Value,
            LastPosition = currentPos,
            LastProgressCheckTick = attempt.ProposedAtTick.Value,
            ConsecutiveStallCount = 0
        };

        return ActionExecutionResult.Started();
    }

    /// <summary>
    /// Executes a stop action (cancels active movements).
    /// </summary>
    public ActionExecutionResult ExecuteStop(ActionAttempt attempt, IEntityManager entityManager, IActiveActionRegistry registry)
    {
        var entity = GetEntityForBody(attempt.BodyId, entityManager);
        if (entity == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");
        }

        // Stop all movement for this body
        StopEntityMovement(entity.Value, entityManager);

        // Cancel any active movements for this body
        var activeForBody = registry.GetActiveForBody(attempt.BodyId)
            .Where(a => a.Capability.GetCategory() == "movement" && a.ProposalId != attempt.ProposalId)
            .ToList();

        foreach (var activeAttempt in activeForBody)
        {
            registry.UpdateState(activeAttempt.ProposalId, ActionState.Cancelled, attempt.ProposedAtTick);
            _activeMovements.Remove(activeAttempt.ProposalId);
            registry.Remove(activeAttempt.ProposalId);
        }

        return ActionExecutionResult.Completed(null);
    }

    /// <summary>
    /// Starts an async move_to_location action using NPCSteeringSystem.
    /// </summary>
    public ActionExecutionResult StartMoveToLocation(ActionAttempt attempt, IEntityManager entityManager)
    {
        var parameters = ActionParameterSerializer.Deserialize<MoveToLocationParams>(attempt.Parameters);
        if (parameters == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid move parameters");
        }

        var entity = GetEntityForBody(attempt.BodyId, entityManager);
        if (entity == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");
        }

        // Check incapacitation
        var incapCheck = CheckIncapacitation(entity.Value, entityManager);
        if (!incapCheck.CanAct)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);
        }

        if (!entityManager.TryGetComponent<TransformComponent>(entity.Value, out var xform))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "No transform component");
        }

        var targetLocalPos = new Vector2((float)parameters.TargetLocation.X, (float)parameters.TargetLocation.Y);
        var currentPos = xform.LocalPosition;
        
        var logger = Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.Log.ILogManager>().GetSawmill("cogr.movement");
        logger.Info("MoveToLocation: entity {0} from {1} to {2}", entity.Value, currentPos, targetLocalPos);

        var arrivalTolerance = parameters.ArrivalTolerance > 0 ? parameters.ArrivalTolerance : DefaultArrivalTolerance;
        var distance = (targetLocalPos - currentPos).Length();

        // Check if already at target
        if (distance <= arrivalTolerance)
        {
            return ActionExecutionResult.Completed(new MovementResultData
            {
                FinalX = currentPos.X,
                FinalY = currentPos.Y,
                ReachedTarget = true,
                DistanceTraveled = 0.0
            });
        }

        // Use NPCSteeringSystem for pathfinding
        if (!entityManager.TrySystem<Content.Server.NPC.Systems.NPCSteeringSystem>(out var steeringSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "NPCSteeringSystem not available");
        }

        // Ensure required components
        EnsureMovementComponents(entity.Value, entityManager);
        
        // Register with steering system
        var targetCoords = new EntityCoordinates(xform.GridUid ?? xform.MapUid ?? EntityUid.Invalid, targetLocalPos);
        steeringSystem.Register(entity.Value, targetCoords);

        // Start tracking active movement
        var speed = parameters.Run ? 4.0 : DefaultSpeed;
        _activeMovements[attempt.ProposalId] = new ActiveMovement
        {
            ProposalId = attempt.ProposalId,
            BodyId = attempt.BodyId,
            AgentId = attempt.AgentId,
            Entity = entity.Value,
            TargetLocation = targetLocalPos,
            ArrivalTolerance = arrivalTolerance,
            Speed = speed,
            DistanceTraveled = 0.0,
            StartTick = attempt.ProposedAtTick.Value,
            LastPosition = currentPos,
            LastProgressCheckTick = attempt.ProposedAtTick.Value,
            ConsecutiveStallCount = 0
        };

        return ActionExecutionResult.Started();
    }

    /// <summary>
    /// Cleans up movement tracking for a specific action.
    /// Called when actions are cancelled, timeout, or fail.
    /// </summary>
    public void CleanupMovement(ActionProposalId proposalId, BodyId bodyId, IEntityManager entityManager)
    {
        if (_activeMovements.Remove(proposalId, out var movement))
        {
            StopEntityMovement(movement.Entity, entityManager);
        }
    }

    /// <summary>
    /// Cleans up all movements for a body (e.g., on connection loss or body replacement).
    /// </summary>
    public IReadOnlyList<ActionProposalId> CleanupAllForBody(BodyId bodyId, IEntityManager entityManager)
    {
        var cleaned = new List<ActionProposalId>();
        var toRemove = _activeMovements
            .Where(kvp => kvp.Value.BodyId == bodyId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var proposalId in toRemove)
        {
            if (_activeMovements.Remove(proposalId, out var movement))
            {
                StopEntityMovement(movement.Entity, entityManager);
                cleaned.Add(proposalId);
            }
        }

        return cleaned;
    }

    /// <summary>
    /// Ticks all active movements and returns terminal results.
    /// </summary>
    public IReadOnlyList<ActionResult> TickMovements(ulong currentTick, IEntityManager entityManager, IActiveActionRegistry registry)
    {
        var results = new List<ActionResult>();
        var completed = new List<ActionProposalId>();

        foreach (var (proposalId, movement) in _activeMovements)
        {
            var result = ProgressMovement(proposalId, movement, currentTick, entityManager, registry);
            if (result != null)
            {
                completed.Add(proposalId);
                results.Add(result);
            }
        }

        foreach (var proposalId in completed)
        {
            _activeMovements.Remove(proposalId);
        }

        return results;
    }

    private ActionResult? ProgressMovement(
        ActionProposalId proposalId,
        ActiveMovement movement,
        ulong currentTick,
        IEntityManager entityManager,
        IActiveActionRegistry registry)
    {
        var entity = movement.Entity;
        var logger = Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.Log.ILogManager>().GetSawmill("cogr.movement");

        // 1. Check if entity still exists (body deletion)
        if (!entityManager.EntityExists(entity))
        {
            logger.Warning("Movement {0}: entity deleted", proposalId);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(proposalId, new SimTick(currentTick), ActionFailureReason.BodyDied, "Entity deleted");
        }

        // 2. Check for body replacement (different body now has this ID)
        var currentEntity = GetEntityForBody(movement.BodyId, entityManager);
        if (currentEntity != entity)
        {
            logger.Warning("Movement {0}: body replaced", proposalId);
            StopEntityMovement(entity, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(proposalId, new SimTick(currentTick), ActionFailureReason.BodyReplaced, "Body replaced");
        }

        // 3. Check transform
        if (!entityManager.TryGetComponent<TransformComponent>(entity, out var xform))
        {
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(proposalId, new SimTick(currentTick), ActionFailureReason.BodyDied, "No transform");
        }

        // 4. Check incapacitation (crit, dead, restrained)
        var incapCheck = CheckIncapacitation(entity, entityManager);
        if (!incapCheck.CanAct)
        {
            logger.Warning("Movement {0}: body incapacitated - {1}", proposalId, incapCheck.Reason);
            StopEntityMovement(entity, entityManager);
            registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            return ActionResult.Failed(proposalId, new SimTick(currentTick), ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);
        }

        // 5. Check timeout
        var ticksElapsed = currentTick - movement.StartTick;
        if (ticksElapsed > MaxMovementTicks)
        {
            logger.Warning("Movement {0}: timed out after {1} ticks", proposalId, ticksElapsed);
            StopEntityMovement(entity, entityManager);
            registry.UpdateState(proposalId, ActionState.TimedOut, new SimTick(currentTick));
            return ActionResult.TimedOut(proposalId, new SimTick(currentTick));
        }

        // 6. Check position and progress
        var currentPos = xform.LocalPosition;
        var targetPos = movement.TargetLocation;
        var distance = (targetPos - currentPos).Length();

        // Check if arrived
        if (distance <= movement.ArrivalTolerance)
        {
            logger.Info("Movement {0}: arrived at target", proposalId);
            StopEntityMovement(entity, entityManager);
            registry.UpdateState(proposalId, ActionState.Completed, new SimTick(currentTick));
            return ActionResult.Completed(proposalId, new SimTick(currentTick), new MovementResultData
            {
                FinalX = currentPos.X,
                FinalY = currentPos.Y,
                ReachedTarget = true,
                DistanceTraveled = movement.DistanceTraveled
            });
        }

        // 7. Check for stall (no progress)
        var ticksSinceLastCheck = currentTick - movement.LastProgressCheckTick;
        if (ticksSinceLastCheck >= StallCheckIntervalTicks)
        {
            var lastPos = movement.LastPosition ?? currentPos;
            var progressSinceLastCheck = (currentPos - lastPos).Length();
            if (progressSinceLastCheck < MinProgressPerCheck)
            {
                movement.ConsecutiveStallCount++;
                logger.Debug("Movement {0}: stall detected ({1} consecutive)", proposalId, movement.ConsecutiveStallCount);
                
                if (movement.ConsecutiveStallCount >= MaxConsecutiveStalls)
                {
                    logger.Warning("Movement {0}: persistent stall - no path or blocked", proposalId);
                    StopEntityMovement(entity, entityManager);
                    registry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
                    return ActionResult.Failed(proposalId, new SimTick(currentTick), ActionFailureReason.NoPathFound, "Persistent stall - path blocked or unreachable");
                }
            }
            else
            {
                // Made progress, reset stall counter
                movement.ConsecutiveStallCount = 0;
            }
            
            movement.LastProgressCheckTick = currentTick;
            movement.LastPosition = currentPos;
        }

        // Update distance traveled
        var lastKnownPos = movement.LastPosition ?? currentPos;
        var movedThisTick = (currentPos - lastKnownPos).Length();
        movement.DistanceTraveled += movedThisTick;
        movement.LastPosition = currentPos;

        // Update state to progressing if not already
        var currentAttempt = registry.GetAction(proposalId);
        if (currentAttempt?.State == ActionState.Started)
        {
            registry.UpdateState(proposalId, ActionState.Progressing, new SimTick(currentTick));
        }

        // Still in progress
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private EntityUid? GetEntityForBody(BodyId bodyId, IEntityManager entityManager)
    {
        var query = entityManager.AllEntityQueryEnumerator<Content.Shared.COGR.Components.COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.BodyId == bodyId.ToGuid())
                return uid;
        }
        return null;
    }

    private void EnsureMovementComponents(EntityUid entity, IEntityManager entityManager)
    {
        entityManager.EnsureComponent<Content.Shared.Movement.Components.InputMoverComponent>(entity);
        entityManager.EnsureComponent<Content.Shared.Movement.Components.MovementSpeedModifierComponent>(entity);
        entityManager.EnsureComponent<Content.Shared.Movement.Components.MobMoverComponent>(entity);
        entityManager.EnsureComponent<Content.Shared.NPC.ActiveNPCComponent>(entity);
    }

    private void StopEntityMovement(EntityUid entity, IEntityManager entityManager)
    {
        // Unregister from NPC steering
        if (entityManager.TrySystem<Content.Server.NPC.Systems.NPCSteeringSystem>(out var steeringSystem))
        {
            steeringSystem.Unregister(entity);
        }

        // Remove ActiveNPCComponent
        entityManager.RemoveComponent<Content.Shared.NPC.ActiveNPCComponent>(entity);

        // Clear movement input
        if (entityManager.TryGetComponent<Content.Shared.Movement.Components.InputMoverComponent>(entity, out var mover))
        {
            mover.CurTickSprintMovement = Vector2.Zero;
            mover.CurTickWalkMovement = Vector2.Zero;
            entityManager.Dirty(entity, mover);
        }
    }

    /// <summary>
    /// Checks if an entity is incapacitated and cannot perform actions.
    /// </summary>
    private IncapacitationCheck CheckIncapacitation(EntityUid entity, IEntityManager entityManager)
    {
        // Check mob state (crit, dead)
        if (entityManager.TryGetComponent<MobStateComponent>(entity, out var mobState))
        {
            if (mobState.CurrentState == MobState.Dead)
            {
                return new IncapacitationCheck(false, "Body is dead");
            }
            if (mobState.CurrentState == MobState.Critical)
            {
                return new IncapacitationCheck(false, "Body is in critical condition");
            }
        }

        // Check if buckled (restrained to furniture)
        if (entityManager.TryGetComponent<BuckleComponent>(entity, out var buckle))
        {
            if (buckle.Buckled)
            {
                return new IncapacitationCheck(false, "Body is buckled/restrained");
            }
        }

        // TODO: Add more incapacitation checks as needed:
        // - Stunned
        // - Paralyzed
        // - Sleeping
        // - In container

        return new IncapacitationCheck(true, null);
    }

    private static Angle DirectionToAngle(global::COGR.Core.Actions.Parameters.Direction direction)
    {
        return direction switch
        {
            global::COGR.Core.Actions.Parameters.Direction.North => Angle.FromDegrees(90),
            global::COGR.Core.Actions.Parameters.Direction.East => Angle.FromDegrees(0),
            global::COGR.Core.Actions.Parameters.Direction.South => Angle.FromDegrees(270),
            global::COGR.Core.Actions.Parameters.Direction.West => Angle.FromDegrees(180),
            global::COGR.Core.Actions.Parameters.Direction.NorthEast => Angle.FromDegrees(45),
            global::COGR.Core.Actions.Parameters.Direction.SouthEast => Angle.FromDegrees(315),
            global::COGR.Core.Actions.Parameters.Direction.SouthWest => Angle.FromDegrees(225),
            global::COGR.Core.Actions.Parameters.Direction.NorthWest => Angle.FromDegrees(135),
            _ => Angle.Zero
        };
    }

    private static Vector2 DirectionToOffset(global::COGR.Core.Actions.Parameters.Direction direction)
    {
        return direction switch
        {
            global::COGR.Core.Actions.Parameters.Direction.North => new Vector2(0, 1),
            global::COGR.Core.Actions.Parameters.Direction.East => new Vector2(1, 0),
            global::COGR.Core.Actions.Parameters.Direction.South => new Vector2(0, -1),
            global::COGR.Core.Actions.Parameters.Direction.West => new Vector2(-1, 0),
            global::COGR.Core.Actions.Parameters.Direction.NorthEast => new Vector2(0.707f, 0.707f),
            global::COGR.Core.Actions.Parameters.Direction.SouthEast => new Vector2(0.707f, -0.707f),
            global::COGR.Core.Actions.Parameters.Direction.SouthWest => new Vector2(-0.707f, -0.707f),
            global::COGR.Core.Actions.Parameters.Direction.NorthWest => new Vector2(-0.707f, 0.707f),
            _ => Vector2.Zero
        };
    }

    private static Vector2 BodyRelativeDirectionToOffset(BodyRelativeDirection direction, Angle localRotation)
    {
        const float diagonal = 0.70710677f;
        var relative = direction switch
        {
            BodyRelativeDirection.Forward => new Vector2(1, 0),
            BodyRelativeDirection.ForwardLeft => new Vector2(diagonal, diagonal),
            BodyRelativeDirection.Left => new Vector2(0, 1),
            BodyRelativeDirection.BackLeft => new Vector2(-diagonal, diagonal),
            BodyRelativeDirection.Back => new Vector2(-1, 0),
            BodyRelativeDirection.BackRight => new Vector2(-diagonal, -diagonal),
            BodyRelativeDirection.Right => new Vector2(0, -1),
            BodyRelativeDirection.ForwardRight => new Vector2(diagonal, -diagonal),
            _ => Vector2.Zero,
        };
        var cos = (float)Math.Cos(localRotation.Theta);
        var sin = (float)Math.Sin(localRotation.Theta);
        return new Vector2(
            relative.X * cos - relative.Y * sin,
            relative.X * sin + relative.Y * cos);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Supporting Types
// ═══════════════════════════════════════════════════════════════════════════

internal sealed class ActiveMovement
{
    public required ActionProposalId ProposalId { get; init; }
    public required BodyId BodyId { get; init; }
    public required AgentId AgentId { get; init; }
    public required EntityUid Entity { get; init; }
    public required Vector2 TargetLocation { get; init; }
    public required double ArrivalTolerance { get; init; }
    public required double Speed { get; init; }
    public double DistanceTraveled { get; set; }
    public required ulong StartTick { get; init; }
    public Vector2? LastPosition { get; set; }
    public ulong LastProgressCheckTick { get; set; }
    public int ConsecutiveStallCount { get; set; }
}

internal readonly struct IncapacitationCheck
{
    public bool CanAct { get; }
    public string? Reason { get; }

    public IncapacitationCheck(bool canAct, string? reason)
    {
        CanAct = canAct;
        Reason = reason;
    }
}