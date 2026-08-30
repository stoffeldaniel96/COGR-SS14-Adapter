using System;
using System.Collections.Generic;
using System.Linq;
using COGR.Contracts.Messages;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Sequences;
using COGR.Core.Time;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRBoundedPerceptionSystem
{
    /// <summary>
    /// Revokes every observer-scoped reference and cached perception handle for an entity that is
    /// entering authoritative termination. The generic world-change router owns the host lifecycle
    /// subscription; bounded perception remains the authority for reference/cache invalidation.
    /// </summary>
    public void NotifyEntityTerminating(EntityUid entity) =>
        InvalidateEntity(entity, "entity_terminated");

    private void InvalidateEntity(EntityUid entity, string reason)
    {
        var batches = _adapter.ReferenceRegistry?.InvalidateForEntity(entity)
            ?? Array.Empty<ReferenceInvalidationBatch>();

        // Only a previously exposed entity can make an existing semantic replica stale here.
        // Route its disappearance before transform teardown, then let the existing owner-scoped
        // invalidation contract revoke the opaque references immediately.
        if (batches.Count > 0 &&
            EntityManager.TrySystem<COGRRegionalPerceptionRouterSystem>(out var regionalRouter))
        {
            regionalRouter.NotifyLocalSemanticChange(entity, "observed_entity_removed");
        }

        RemoveCachedReferences(key => key.Entity == entity);
        RemoveCachedSubreferents(key => key.ParentEntity == entity);
        EmitInvalidations(batches, reason);
    }

    private void EmitInvalidations(
        IReadOnlyList<ReferenceInvalidationBatch> batches,
        string reason)
    {
        if (batches.Count == 0 ||
            _adapter.Connection is not { IsConnected: true } connection ||
            connection.ConnectionId == Guid.Empty ||
            !_authority.BoundWorld.HasValue)
        {
            return;
        }

        var activeConnectionId = ConnectionId.FromGuid(connection.ConnectionId);
        var currentTick = new SimTick((ulong)_timing.CurTick.Value);
        foreach (var batch in batches)
        {
            if (batch.ConnectionId != activeConnectionId || batch.References.Count == 0)
                continue;

            connection.EnqueueEnvironmentMessage(new ReferenceInvalidationMessage
            {
                WorldId = _authority.BoundWorld.Value,
                ConnectionId = batch.ConnectionId,
                Tick = currentTick,
                SourceSequence = SourceSequence.Unassigned,
                LatestAck = default,
                AgentId = batch.AgentId,
                InvalidatedReferences = batch.References,
                Reason = reason,
            });
        }
    }

    private void RemoveCachedReferences(Func<ReferenceCacheKey, bool> predicate)
    {
        var staleKeys = _referenceCache.Keys
            .Concat(_passiveVisualSemanticFingerprints.Keys)
            .Where(predicate)
            .Distinct()
            .ToList();
        foreach (var key in staleKeys)
        {
            _referenceCache.Remove(key);
            _passiveVisualSemanticFingerprints.Remove(key);
        }
    }
}
