using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using Content.Server.COGR.Systems;

namespace Content.Server.COGR.Actions;

public sealed partial class COGRActionExecutor
{
    private readonly COGRSustainedOrientationHandler _sustainedOrientationHandler = new();

    private ActionExecutionResult ExecuteMaintainOrientation(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<MaintainOrientationToReferenceParams>(attempt.Parameters);
        if (parameters is null)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid sustained orientation parameters");
        if (_referenceResolver is null)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Reference resolver not configured");

        var replica = EntityManager.System<COGRSemanticReplicaSystem>();
        return _sustainedOrientationHandler.Start(
            attempt,
            EntityManager,
            parameters.TargetRef,
            (currentAttempt, reference) => _referenceResolver(currentAttempt, reference),
            replica.IsReferenceCurrentlyObserved);
    }

    private IReadOnlyList<ActionResult> TickSustainedOrientations(ulong currentTick)
    {
        if (_referenceResolver is null)
            return Array.Empty<ActionResult>();

        var replica = EntityManager.System<COGRSemanticReplicaSystem>();
        return _sustainedOrientationHandler.Tick(
            currentTick,
            EntityManager,
            _actionRegistry,
            (attempt, reference) => _referenceResolver(attempt, reference),
            replica.IsReferenceCurrentlyObserved,
            attempt => ValidateAuthority(attempt).IsValid);
    }

    private static CapabilityValidationResult ValidateMaintainOrientationParams(ReadOnlyMemory<byte> parameters)
    {
        var value = ActionParameterSerializer.Deserialize<MaintainOrientationToReferenceParams>(parameters);
        if (value is null || !value.TargetRef.IsAssigned)
        {
            return CapabilityValidationResult.Invalid(
                ActionRejectionReason.InvalidParameters,
                "Invalid sustained orientation parameters");
        }

        return CapabilityValidationResult.Valid();
    }
}
