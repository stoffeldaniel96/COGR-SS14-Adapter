using System.Linq;
using COGR.Core.Actions;
using COGR.Core.Identifiers;
using COGR.Core.Time;
using Content.Server.DoAfter;
using Content.Shared.COGR.Components;
using Content.Shared.DoAfter;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Strip.Components;
using Robust.Shared.Containers;

namespace Content.Server.COGR.Actions;

/// <summary>
/// Adapter-local SS14 realization for semantic acquisition.
/// </summary>
/// <remarks>
/// This class selects only existing host mechanics. It does not define COGR action semantics and it does not
/// modify SS14 stripping behavior. Loose items use native hand pickup. Items held by another strippable actor
/// are routed through the same stripping slot event used by the native UI, then correlated to the resulting
/// native DoAfter. Other custody mechanisms fail closed until the adapter has an equivalent native mapping.
/// </remarks>
internal sealed class COGRAcquisitionHandler
{
    private const float InteractionRange = 1.5f;
    private readonly Dictionary<ActionProposalId, ActiveAcquisition> _active = new();

    public ActionExecutionResult Execute(
        ActionAttempt attempt,
        IEntityManager entityManager,
        EnvironmentRef targetRef,
        Func<EnvironmentRef, EntityUid?> resolveReference)
    {
        var actor = ResolveBody(attempt.BodyId, entityManager);
        if (!actor.HasValue)
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");

        var target = resolveReference(targetRef);
        if (!target.HasValue)
            return ActionExecutionResult.Failed(ActionFailureReason.TargetRemoved, "Target reference could not be resolved");

        if (!entityManager.HasComponent<ItemComponent>(target.Value))
            return ActionExecutionResult.Failed(ActionFailureReason.TargetStateChanged, "Acquisition target is not an item");

        if (!entityManager.TrySystem<SharedHandsSystem>(out var handsSystem))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "HandsSystem not available");
        if (!entityManager.TryGetComponent<HandsComponent>(actor.Value, out var actorHands))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Actor has no hands");
        if (!entityManager.TrySystem<SharedContainerSystem>(out var containerSystem))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "ContainerSystem not available");
        if (!entityManager.TrySystem<SharedInteractionSystem>(out var interactionSystem))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "InteractionSystem not available");

        if (handsSystem.IsHolding((actor.Value, actorHands), target.Value))
            return Completed(itemInHand: true);

        if (!containerSystem.TryGetContainingContainer((target.Value, null, null), out var containingContainer))
        {
            if (!interactionSystem.InRangeUnobstructed(actor.Value, target.Value, InteractionRange))
                return ActionExecutionResult.Failed(ActionFailureReason.TargetMovedOutOfRange, "Acquisition target is out of range");

            if (!handsSystem.TryPickupAnyHand(actor.Value, target.Value, checkActionBlocker: true, handsComp: actorHands))
            {
                return ActionExecutionResult.Failed(
                    ActionFailureReason.InteractionBlocked,
                    "Native pickup rejected acquisition");
            }

            return Completed(itemInHand: true);
        }

        var holder = containingContainer.Owner;
        if (holder == actor.Value)
            return Completed(handsSystem.IsHolding((actor.Value, actorHands), target.Value));

        if (!entityManager.TryGetComponent<HandsComponent>(holder, out var holderHands)
            || !handsSystem.IsHolding((holder, holderHands), target.Value, out var holderHand)
            || holderHand is null)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.InteractionBlocked,
                "Acquisition target is contained by an unsupported custody mechanism");
        }

        if (!entityManager.HasComponent<StrippableComponent>(holder)
            || !entityManager.HasComponent<StrippingComponent>(actor.Value))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.InteractionBlocked,
                "Native stripping is not available for this actor or holder");
        }

        if (!interactionSystem.InRangeUnobstructed(actor.Value, holder, InteractionRange))
            return ActionExecutionResult.Failed(ActionFailureReason.TargetMovedOutOfRange, "Target holder is out of range");

        var existingDoAfters = entityManager.TryGetComponent<DoAfterComponent>(actor.Value, out var beforeDoAfter)
            ? beforeDoAfter.DoAfters.Keys.ToHashSet()
            : new HashSet<ushort>();

        // Raise the same local message that a native stripping UI button produces. Actor is normally populated
        // by Robust's BUI receive path; the adapter supplies it explicitly because no client UI is involved.
        var stripMessage = new StrippingSlotButtonPressed(holderHand, isHand: true)
        {
            Actor = actor.Value,
            UiKey = StrippingUiKey.Key,
        };
        entityManager.EventBus.RaiseLocalEvent(holder, (object) stripMessage);

        // Instant DoAfters do not remain in DoAfterComponent, so verify possession first.
        if (handsSystem.IsHolding((actor.Value, actorHands), target.Value))
            return Completed(itemInHand: true);

        if (!entityManager.TryGetComponent<DoAfterComponent>(actor.Value, out var afterDoAfter))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.InteractionBlocked,
                "Native stripping did not start an acquisition lifecycle");
        }

        var nativeDoAfter = afterDoAfter.DoAfters.Values
            .Where(value => !existingDoAfters.Contains(value.Index))
            .FirstOrDefault(value =>
                value.Args.User == actor.Value
                && value.Args.Target == holder
                && value.Args.Used == target.Value
                && value.Args.Event is StrippableDoAfterEvent strip
                && !strip.InsertOrRemove
                && !strip.InventoryOrHand
                && strip.SlotOrHandName == holderHand);

        if (nativeDoAfter is null)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.InteractionBlocked,
                "Native stripping rejected acquisition");
        }

        _active[attempt.ProposalId] = new ActiveAcquisition(
            actor.Value,
            holder,
            target.Value,
            new DoAfterId(actor.Value, nativeDoAfter.Index));

        return ActionExecutionResult.Started();
    }

    public IReadOnlyList<ActionResult> Tick(
        ulong currentTick,
        IEntityManager entityManager,
        IActiveActionRegistry registry)
    {
        if (_active.Count == 0)
            return Array.Empty<ActionResult>();

        var tick = new SimTick(currentTick);
        var results = new List<ActionResult>();
        var completed = new List<ActionProposalId>();

        if (!entityManager.TrySystem<SharedHandsSystem>(out var handsSystem)
            || !entityManager.TrySystem<DoAfterSystem>(out var doAfterSystem))
        {
            foreach (var proposalId in _active.Keys)
            {
                registry.UpdateState(proposalId, ActionState.Failed, tick);
                results.Add(ActionResult.Failed(
                    proposalId,
                    tick,
                    ActionFailureReason.Unspecified,
                    "Native acquisition lifecycle system became unavailable"));
                completed.Add(proposalId);
            }

            RemoveCompleted(completed);
            return results;
        }

        foreach (var (proposalId, acquisition) in _active)
        {
            var attempt = registry.GetAction(proposalId);
            if (attempt is null || attempt.State.IsTerminal())
            {
                completed.Add(proposalId);
                continue;
            }

            ActionResult? result = null;

            if (!entityManager.EntityExists(acquisition.Actor))
            {
                result = ActionResult.Failed(
                    proposalId,
                    tick,
                    ActionFailureReason.BodyDied,
                    "Acquiring body no longer exists");
            }
            else if (!entityManager.EntityExists(acquisition.Target))
            {
                result = ActionResult.Failed(
                    proposalId,
                    tick,
                    ActionFailureReason.TargetRemoved,
                    "Acquisition target no longer exists");
            }
            else if (handsSystem.IsHolding(acquisition.Actor, acquisition.Target))
            {
                result = ActionResult.Completed(
                    proposalId,
                    tick,
                    new ManipulationResultData
                    {
                        Success = true,
                        ItemInHand = true,
                    },
                    "Native acquisition completed");
            }
            else
            {
                var status = doAfterSystem.GetStatus(acquisition.DoAfterId);
                switch (status)
                {
                    case DoAfterStatus.Running:
                        if (attempt.State == ActionState.Started)
                            registry.UpdateState(proposalId, ActionState.Progressing, tick);
                        break;
                    case DoAfterStatus.Cancelled:
                        result = ActionResult.Failed(
                            proposalId,
                            tick,
                            ActionFailureReason.ExternalInterruption,
                            "Native stripping acquisition was interrupted");
                        break;
                    case DoAfterStatus.Finished:
                        result = ActionResult.Failed(
                            proposalId,
                            tick,
                            ActionFailureReason.TargetStateChanged,
                            "Native stripping finished without acquiring the target");
                        break;
                    case DoAfterStatus.Invalid:
                        result = ActionResult.Failed(
                            proposalId,
                            tick,
                            entityManager.EntityExists(acquisition.Holder)
                                && handsSystem.IsHolding(acquisition.Holder, acquisition.Target)
                                    ? ActionFailureReason.ExternalInterruption
                                    : ActionFailureReason.TargetStateChanged,
                            "Native stripping lifecycle ended before acquisition completed");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            if (result is null)
                continue;

            registry.UpdateState(proposalId, result.TerminalState, tick);
            results.Add(result);
            completed.Add(proposalId);
        }

        RemoveCompleted(completed);
        return results;
    }

    public void Cleanup(ActionProposalId proposalId, IEntityManager entityManager)
    {
        if (!_active.Remove(proposalId, out var acquisition))
            return;

        if (entityManager.TrySystem<DoAfterSystem>(out var doAfterSystem)
            && doAfterSystem.IsRunning(acquisition.DoAfterId))
        {
            doAfterSystem.Cancel(acquisition.DoAfterId);
        }
    }

    private void RemoveCompleted(IEnumerable<ActionProposalId> proposalIds)
    {
        foreach (var proposalId in proposalIds)
            _active.Remove(proposalId);
    }

    private static ActionExecutionResult Completed(bool itemInHand) =>
        ActionExecutionResult.Completed(new ManipulationResultData
        {
            Success = true,
            ItemInHand = itemInHand,
        });

    private static EntityUid? ResolveBody(BodyId bodyId, IEntityManager entityManager)
    {
        var query = entityManager.AllEntityQueryEnumerator<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var controlled))
        {
            if (controlled.BodyId == bodyId.ToGuid())
                return uid;
        }

        return null;
    }

    private sealed record ActiveAcquisition(
        EntityUid Actor,
        EntityUid Holder,
        EntityUid Target,
        DoAfterId DoAfterId);
}
