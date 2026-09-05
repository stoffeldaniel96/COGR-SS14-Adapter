using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Time;
using Content.Server.COGR.Systems;

namespace Content.Server.COGR.Actions;

public sealed partial class COGRActionExecutor
{
    private readonly COGRRelativeSpatialMovementHandler _relativeSpatialMovementHandler = new();

    private ActionExecutionResult ExecuteEstablishSpatialRelation(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<EstablishSpatialRelationParams>(attempt.Parameters);
        if (parameters is null)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid relative-spatial movement parameters");
        if (_referenceResolver is null)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Reference resolver not configured");

        Func<EnvironmentRef, bool>? isCurrentlyObserved = null;
        Func<bool>? hasCurrentAuthority = null;
        if (parameters.Maintain)
        {
            var replica = EntityManager.System<COGRSemanticReplicaSystem>();
            bool? previousObserved = null;
            isCurrentlyObserved = reference =>
            {
                var observed = replica.IsReferenceCurrentlyVisuallyAvailable(attempt, reference);
                if (Content.Server.COGR.COGRAdapterTrace.Enabled && previousObserved != observed)
                {
                    _sawmill.Info(
                        "[PROMPTED] relative-spatial visibility proposal={0} body={1} targetRef={2} observed={3}",
                        attempt.ProposalId,
                        attempt.BodyId,
                        reference,
                        observed);
                }

                previousObserved = observed;
                return observed;
            };
            hasCurrentAuthority = () => ValidateAuthority(attempt).IsValid;
        }

        if (Content.Server.COGR.COGRAdapterTrace.Enabled)
        {
            var resolvedTarget = _referenceResolver(attempt, parameters.TargetRef);
            _sawmill.Info(
                "[PROMPTED] relative-spatial target-binding proposal={0} body={1} targetRef={2} target={3} maintain={4}",
                attempt.ProposalId,
                attempt.BodyId,
                parameters.TargetRef,
                resolvedTarget?.ToString() ?? "<null>",
                parameters.Maintain);
        }

        return _relativeSpatialMovementHandler.Start(
            attempt,
            EntityManager,
            parameters.TargetRef,
            reference => _referenceResolver(attempt, reference),
            isCurrentlyObserved,
            hasCurrentAuthority);
    }

    private ActionExecutionResult ExecuteMovementStop(ActionAttempt attempt)
    {
        // A stop request interrupts only adapter-owned locomotion realizations. Body orientation is an independent
        // physical control channel and must survive unless it is explicitly cancelled or otherwise invalidated.
        var activeLocomotion = _actionRegistry.GetActiveForBodyClaimingChannel(
            attempt.BodyId,
            COGRPhysicalControlChannel.Locomotion);
        var tick = new SimTick((ulong)_timing.CurTick.Value);

        foreach (var activeAttempt in activeLocomotion)
        {
            if (activeAttempt.ProposalId == attempt.ProposalId)
                continue;

            CleanupLocomotionTracking(activeAttempt.ProposalId, activeAttempt.BodyId);
            _actionRegistry.UpdateState(activeAttempt.ProposalId, ActionState.Cancelled, tick);
            _actionRegistry.Remove(activeAttempt.ProposalId);
        }

        return ActionExecutionResult.Completed(null);
    }

    private static CapabilityValidationResult ValidateEstablishSpatialRelationParams(ReadOnlyMemory<byte> parameters)
    {
        var value = ActionParameterSerializer.Deserialize<EstablishSpatialRelationParams>(parameters);
        if (value is null
            || value.TargetRef.Value == Guid.Empty
            || value.Relation != RelativeSpatialRelation.WithinReach
            || !Enum.IsDefined(value.RoutePreference)
            || !Enum.IsDefined(value.TerminalOrientation))
        {
            return CapabilityValidationResult.Invalid(
                ActionRejectionReason.InvalidParameters,
                "Invalid bounded relative-spatial movement parameters");
        }

        return CapabilityValidationResult.Valid();
    }
}
