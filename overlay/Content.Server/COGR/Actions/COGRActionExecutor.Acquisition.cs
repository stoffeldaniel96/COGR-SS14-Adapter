using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;

namespace Content.Server.COGR.Actions;

public sealed partial class COGRActionExecutor
{
    private readonly COGRAcquisitionHandler _acquisitionHandler = new();

    private ActionExecutionResult ExecuteAcquire(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<PickUpActionParams>(attempt.Parameters);
        if (parameters is null)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid acquisition parameters");
        if (_referenceResolver is null)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Reference resolver not configured");

        return _acquisitionHandler.Execute(
            attempt,
            EntityManager,
            parameters.TargetRef,
            reference => _referenceResolver(attempt, reference));
    }

    private IReadOnlyList<ActionResult> TickAcquisitions(ulong currentTick) =>
        _acquisitionHandler.Tick(currentTick, EntityManager, _actionRegistry);

    private void CleanupAcquisition(ActionProposalId proposalId) =>
        _acquisitionHandler.Cleanup(proposalId, EntityManager);
}
