using System.Linq;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using EnvironmentReferenceId = COGR.Core.Identifiers.EnvironmentRef;
using COGR.Core.Time;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Buckle.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map;

namespace Content.Server.COGR.Actions;

/// <summary>
/// F02/F03 implementation of interaction action handler for COGR-controlled entities.
/// Handles open, close, pick_up, drop, and place_near actions using native SS14 systems.
/// </summary>
/// <remarks>
/// Per COGR-DES-005 Workstream H requirements:
/// - Calls relevant native SS14 systems or raises appropriate events
/// - Preserves access, range, hand, incapacitation, timing, and interruption checks
/// - Reports rejection separately from started-then-failed behavior
/// - Returns authoritative outcomes
/// - Does NOT directly mutate components to pass tests
/// </remarks>
public sealed class COGRInteractionHandler
{
    private const double DefaultInteractionRange = 1.5; // Same as SharedInteractionSystem.InteractionRange
    
    /// <summary>
    /// Executes an open interaction on a door-like entity.
    /// </summary>
    public ActionExecutionResult ExecuteOpen(
        ActionAttempt attempt,
        IEntityManager entityManager,
        EnvironmentReferenceId targetRef,
        Func<EnvironmentReferenceId, EntityUid?> resolveReference)
    {
        var logger = Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.Log.ILogManager>().GetSawmill("cogr.interaction");
        
        // Get the actor entity
        var actor = GetEntityForBody(attempt.BodyId, entityManager);
        if (actor == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");
        }

        // Check incapacitation
        var incapCheck = CheckIncapacitation(actor.Value, entityManager);
        if (!incapCheck.CanAct)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);
        }

        // Resolve the target reference
        var target = resolveReference(targetRef);
        if (target == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetRemoved, "Target reference could not be resolved");
        }

        // Check range using native SS14 interaction system
        if (!entityManager.TrySystem<SharedInteractionSystem>(out var interactionSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "InteractionSystem not available");
        }

        if (!interactionSystem.InRangeUnobstructed(actor.Value, target.Value, (float)DefaultInteractionRange))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetMovedOutOfRange, "Target out of range");
        }

        // Check if target is a door
        if (!entityManager.TryGetComponent<DoorComponent>(target.Value, out var door))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetStateChanged, "Target is not a door");
        }

        // Use native door system to open
        if (!entityManager.TrySystem<SharedDoorSystem>(out var doorSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "DoorSystem not available");
        }

        // Check current door state
        if (door.State == DoorState.Open)
        {
            // Already open - immediate success
            return ActionExecutionResult.Completed(new InteractionResultData
            {
                Success = true,
                ResultState = "already_open"
            });
        }

        // Try to open using native system (respects access, bolts, power, etc.)
        if (!doorSystem.TryOpen(target.Value, door, actor.Value, predicted: false))
        {
            logger.Debug("Open failed for door {0} by actor {1}", target.Value, actor.Value);
            return ActionExecutionResult.Failed(ActionFailureReason.InteractionBlocked, "Cannot open door (access denied, bolted, or no power)");
        }

        logger.Info("Door {0} opened by COGR actor {1}", target.Value, actor.Value);
        return ActionExecutionResult.Completed(new InteractionResultData
        {
            Success = true,
            ResultState = "opened"
        });
    }

    /// <summary>
    /// Executes a close interaction on a door-like entity.
    /// </summary>
    public ActionExecutionResult ExecuteClose(
        ActionAttempt attempt,
        IEntityManager entityManager,
        EnvironmentReferenceId targetRef,
        Func<EnvironmentReferenceId, EntityUid?> resolveReference)
    {
        var logger = Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.Log.ILogManager>().GetSawmill("cogr.interaction");
        
        var actor = GetEntityForBody(attempt.BodyId, entityManager);
        if (actor == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");
        }

        var incapCheck = CheckIncapacitation(actor.Value, entityManager);
        if (!incapCheck.CanAct)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);
        }

        var target = resolveReference(targetRef);
        if (target == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetRemoved, "Target reference could not be resolved");
        }

        if (!entityManager.TrySystem<SharedInteractionSystem>(out var interactionSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "InteractionSystem not available");
        }

        if (!interactionSystem.InRangeUnobstructed(actor.Value, target.Value, (float)DefaultInteractionRange))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetMovedOutOfRange, "Target out of range");
        }

        if (!entityManager.TryGetComponent<DoorComponent>(target.Value, out var door))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetStateChanged, "Target is not a door");
        }

        if (!entityManager.TrySystem<SharedDoorSystem>(out var doorSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "DoorSystem not available");
        }

        if (door.State == DoorState.Closed)
        {
            return ActionExecutionResult.Completed(new InteractionResultData
            {
                Success = true,
                ResultState = "already_closed"
            });
        }

        if (!doorSystem.TryClose(target.Value, door, actor.Value, predicted: false))
        {
            logger.Debug("Close failed for door {0} by actor {1}", target.Value, actor.Value);
            return ActionExecutionResult.Failed(ActionFailureReason.InteractionBlocked, "Cannot close door (obstructed or access denied)");
        }

        logger.Info("Door {0} closed by COGR actor {1}", target.Value, actor.Value);
        return ActionExecutionResult.Completed(new InteractionResultData
        {
            Success = true,
            ResultState = "closed"
        });
    }

    /// <summary>
    /// Executes a pick up manipulation on a loose world item.
    /// </summary>
    public ActionExecutionResult ExecutePickUp(
        ActionAttempt attempt,
        IEntityManager entityManager,
        EnvironmentReferenceId targetRef,
        Func<EnvironmentReferenceId, EntityUid?> resolveReference)
    {
        var logger = Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.Log.ILogManager>().GetSawmill("cogr.interaction");
        
        var actor = GetEntityForBody(attempt.BodyId, entityManager);
        if (actor == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");
        }

        var incapCheck = CheckIncapacitation(actor.Value, entityManager);
        if (!incapCheck.CanAct)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);
        }

        var target = resolveReference(targetRef);
        if (target == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetRemoved, "Target reference could not be resolved");
        }

        // Check if target is an item.
        if (!entityManager.TryGetComponent<ItemComponent>(target.Value, out _))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetStateChanged, "Target is not an item");
        }

        // manipulation.pick_up is deliberately a loose-world-item primitive. Native TryPickupAnyHand can remove an
        // entity from another removable container, including another mob's hand, which would bypass the dedicated
        // SS14 stripping/storage mechanics that a player must use. Contained-item acquisition requires a different
        // host action and must not be silently promoted here.
        if (!entityManager.TrySystem<SharedContainerSystem>(out var containerSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "ContainerSystem not available");
        }

        if (containerSystem.TryGetContainingContainer((target.Value, null, null), out _))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.InteractionBlocked,
                "Cannot pick up item directly while it is contained");
        }

        // Check range.
        if (!entityManager.TrySystem<SharedInteractionSystem>(out var interactionSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "InteractionSystem not available");
        }

        if (!interactionSystem.InRangeUnobstructed(actor.Value, target.Value, (float)DefaultInteractionRange))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.TargetMovedOutOfRange, "Target out of range");
        }

        // Check if actor has hands.
        if (!entityManager.TryGetComponent<HandsComponent>(actor.Value, out var hands))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Actor has no hands");
        }

        // Use native hands system only after proving the target is loose.
        if (!entityManager.TrySystem<SharedHandsSystem>(out var handsSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "HandsSystem not available");
        }

        // TryPickupAnyHand now handles only the ordinary loose-item pickup attempt here; it still enforces action
        // blockers, hand availability, item restrictions, and the final native insertion checks.
        if (!handsSystem.TryPickupAnyHand(actor.Value, target.Value, checkActionBlocker: true, handsComp: hands))
        {
            logger.Debug("Pickup failed for item {0} by actor {1}", target.Value, actor.Value);
            return ActionExecutionResult.Failed(ActionFailureReason.InteractionBlocked, "Cannot pick up item (no free hand, blocked, or item restrictions)");
        }

        logger.Info("Item {0} picked up by COGR actor {1}", target.Value, actor.Value);
        return ActionExecutionResult.Completed(new ManipulationResultData
        {
            Success = true,
            ItemInHand = true
        });
    }

    /// <summary>
    /// Executes a drop manipulation to drop the currently held item.
    /// </summary>
    public ActionExecutionResult ExecuteDrop(
        ActionAttempt attempt,
        IEntityManager entityManager,
        string? handId = null)
    {
        var logger = Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.Log.ILogManager>().GetSawmill("cogr.interaction");
        
        var actor = GetEntityForBody(attempt.BodyId, entityManager);
        if (actor == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");
        }

        var incapCheck = CheckIncapacitation(actor.Value, entityManager);
        if (!incapCheck.CanAct)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);
        }

        if (!entityManager.TryGetComponent<HandsComponent>(actor.Value, out var hands))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Actor has no hands");
        }

        if (!entityManager.TrySystem<SharedHandsSystem>(out var handsSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "HandsSystem not available");
        }

        // Use specified hand or active hand
        var targetHand = handId ?? hands.ActiveHandId;
        if (targetHand == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.ItemDropped, "No hand selected");
        }

        // Get the item in the hand using TryGetHeldItem
        if (!handsSystem.TryGetHeldItem((actor.Value, hands), targetHand, out var heldItem))
        {
            // Nothing to drop - could be considered success
            return ActionExecutionResult.Completed(new ManipulationResultData
            {
                Success = true,
                ItemInHand = false
            });
        }

        // TryDrop respects action blockers, unremoveable items, etc.
        if (!handsSystem.TryDrop((actor.Value, hands), heldItem.Value, checkActionBlocker: true))
        {
            logger.Debug("Drop failed for item {0} by actor {1}", heldItem, actor.Value);
            return ActionExecutionResult.Failed(ActionFailureReason.InteractionBlocked, "Cannot drop item (blocked or unremoveable)");
        }

        logger.Info("Item {0} dropped by COGR actor {1}", heldItem, actor.Value);
        return ActionExecutionResult.Completed(new ManipulationResultData
        {
            Success = true,
            ItemInHand = false
        });
    }

    /// <summary>
    /// Executes a place near manipulation to drop an item near a target location or entity.
    /// </summary>
    public ActionExecutionResult ExecutePlaceNear(
        ActionAttempt attempt,
        IEntityManager entityManager,
        EnvironmentReferenceId? targetRef,
        Func<EnvironmentReferenceId, EntityUid?> resolveReference,
        double? x = null,
        double? y = null)
    {
        var logger = Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.Log.ILogManager>().GetSawmill("cogr.interaction");
        
        var actor = GetEntityForBody(attempt.BodyId, entityManager);
        if (actor == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");
        }

        var incapCheck = CheckIncapacitation(actor.Value, entityManager);
        if (!incapCheck.CanAct)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.BodyBecameIncapacitated, incapCheck.Reason);
        }

        if (!entityManager.TryGetComponent<HandsComponent>(actor.Value, out var hands))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Actor has no hands");
        }

        if (!entityManager.TrySystem<SharedHandsSystem>(out var handsSystem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "HandsSystem not available");
        }

        // Get active hand's item
        var activeHand = hands.ActiveHandId;
        if (activeHand == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.ItemDropped, "No active hand");
        }

        if (!handsSystem.TryGetHeldItem((actor.Value, hands), activeHand, out var heldItem))
        {
            return ActionExecutionResult.Failed(ActionFailureReason.ItemDropped, "No item in hand to place");
        }

        // Determine target location
        EntityUid? targetEntity = null;
        if (targetRef.HasValue)
        {
            targetEntity = resolveReference(targetRef.Value);
            if (targetEntity == null)
            {
                return ActionExecutionResult.Failed(ActionFailureReason.TargetRemoved, "Target reference could not be resolved");
            }

            // Check range to target
            if (!entityManager.TrySystem<SharedInteractionSystem>(out var interactionSystem))
            {
                return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "InteractionSystem not available");
            }

            if (!interactionSystem.InRangeUnobstructed(actor.Value, targetEntity.Value, (float)DefaultInteractionRange))
            {
                return ActionExecutionResult.Failed(ActionFailureReason.TargetMovedOutOfRange, "Target out of range");
            }
        }

        // Drop the item first
        if (!handsSystem.TryDrop((actor.Value, hands), heldItem.Value, checkActionBlocker: true))
        {
            logger.Debug("Place near: drop failed for item {0} by actor {1}", heldItem, actor.Value);
            return ActionExecutionResult.Failed(ActionFailureReason.InteractionBlocked, "Cannot drop item");
        }

        // If target entity provided, use PlaceNextTo
        if (targetEntity.HasValue)
        {
            if (entityManager.TrySystem<SharedTransformSystem>(out var transformSystem))
            {
                transformSystem.PlaceNextTo(heldItem.Value, targetEntity.Value);
            }
        }
        // If coordinates provided, move to those coordinates
        else if (x.HasValue && y.HasValue)
        {
            if (entityManager.TryGetComponent<TransformComponent>(actor.Value, out var actorXform) &&
                entityManager.TrySystem<SharedTransformSystem>(out var transformSystem))
            {
                var targetCoords = new EntityCoordinates(
                    actorXform.GridUid ?? actorXform.MapUid ?? EntityUid.Invalid,
                    new System.Numerics.Vector2((float)x.Value, (float)y.Value));
                transformSystem.SetCoordinates(heldItem.Value, targetCoords);
            }
        }

        logger.Info("Item {0} placed near target by COGR actor {1}", heldItem.Value, actor.Value);
        return ActionExecutionResult.Completed(new ManipulationResultData
        {
            Success = true,
            ItemInHand = false
        });
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

        return new IncapacitationCheck(true, null);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Result Data Types
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Result data for interaction actions (open/close).
/// </summary>
public sealed record InteractionResultData : ActionResultData
{
    public bool Success { get; init; }
    public string? ResultState { get; init; }
}

/// <summary>
/// Result data for manipulation actions (pick_up/drop/place_near).
/// </summary>
public sealed record ManipulationResultData : ActionResultData
{
    public bool Success { get; init; }
    public bool ItemInHand { get; init; }
}
