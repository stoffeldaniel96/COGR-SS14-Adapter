using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using Content.Shared.COGR.Components;
using Content.Shared.Nutrition.EntitySystems;

namespace Content.Server.COGR.Actions;

public sealed partial class COGRActionExecutor
{
    private static CapabilityValidationResult ValidateIngestParams(ReadOnlyMemory<byte> parameters)
    {
        var value = ActionParameterSerializer.Deserialize<IngestActionParams>(parameters);
        return value is not null && value.TargetRef.Value != Guid.Empty
            ? CapabilityValidationResult.Valid()
            : CapabilityValidationResult.Invalid(ActionRejectionReason.InvalidParameters, "Invalid interaction.ingest parameters");
    }

    private ActionExecutionResult ExecuteIngestInteraction(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<IngestActionParams>(attempt.Parameters);
        if (parameters is null)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid interaction.ingest parameters");
        if (_referenceResolver is null)
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Reference resolver not configured");

        var target = _referenceResolver(attempt, parameters.TargetRef);
        if (!target.HasValue)
            return ActionExecutionResult.Failed(ActionFailureReason.TargetRemoved, "Target reference could not be resolved");

        var actor = ResolveControlledBodyForIngest(attempt);
        if (!actor.HasValue)
            return ActionExecutionResult.Failed(ActionFailureReason.BodyAuthorityRevoked, "Controlled body authority no longer matches");
        if (!EntityManager.TrySystem<IngestionSystem>(out var ingestion))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Ingestion system unavailable");
        if (!ingestion.CanIngest(actor.Value, target.Value) || !ingestion.TryIngest(actor.Value, target.Value))
            return ActionExecutionResult.Failed(ActionFailureReason.InteractionBlocked, "Native ingestion rules rejected the target");

        return ActionExecutionResult.Completed(new InteractionResultData
        {
            Success = true,
            ResultState = "ingestion_started"
        });
    }

    private EntityUid? ResolveControlledBodyForIngest(ActionAttempt attempt)
    {
        var query = EntityQueryEnumerator<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var controlled))
        {
            if (!controlled.IsActive ||
                controlled.AgentId != attempt.AgentId.ToGuid() ||
                controlled.BodyId != attempt.BodyId.ToGuid())
                continue;

            var authority = GetBodyAuthority(attempt.BodyId);
            if (!authority.HasValue ||
                authority.Value.AgentId != attempt.AgentId ||
                authority.Value.ConnectionId != attempt.AuthorityLease.ConnectionId ||
                authority.Value.Generation != attempt.AuthorityLease.Generation)
                return null;

            return uid;
        }

        return null;
    }
}
