using System.Linq;
using COGR.Core.Actions;
using COGR.Core.Identifiers;
using COGR.Core.Time;

namespace Content.Server.COGR.Actions;

public sealed partial class COGRActionExecutor
{
    /// <summary>
    /// Rotates body authority and fails every still-active action owned by that body.
    /// </summary>
    /// <remarks>
    /// Authority invalidation is an execution event, not only a lease bookkeeping event.
    /// Active native realizations must stop and every accepted proposal must receive one
    /// terminal result before the bridge context is cleared.
    /// </remarks>
    public IReadOnlyList<ActionResult> RevokeBodyAuthorityAndFailActions(
        BodyId bodyId,
        ActionFailureReason failureReason,
        string detail)
    {
        RevokeBodyAuthority(bodyId);

        var tick = new SimTick((ulong)_timing.CurTick.Value);
        var active = _actionRegistry.GetActiveForBody(bodyId).ToList();
        var results = new List<ActionResult>(active.Count);

        foreach (var attempt in active)
        {
            CleanupActionTracking(attempt.ProposalId, bodyId);
            _actionRegistry.UpdateState(attempt.ProposalId, ActionState.Failed, tick);
            _actionRegistry.Remove(attempt.ProposalId);
            results.Add(ActionResult.Failed(
                attempt.ProposalId,
                tick,
                failureReason,
                detail));
        }

        return results;
    }

    /// <summary>
    /// Removes adapter-native tracking after another action lifecycle path terminates a proposal.
    /// </summary>
    public void CleanupActionTracking(ActionProposalId proposalId, BodyId bodyId)
    {
        _movementHandler.CleanupMovement(proposalId, bodyId, EntityManager);
        _relativeSpatialMovementHandler.CleanupMovement(proposalId, EntityManager);
        CleanupDirectionalSteering(proposalId);
        CleanupProjectedObjectiveSteering(proposalId);
        CleanupAcquisition(proposalId);
    }
}
