using System;
using System.Collections.Generic;
using System.Linq;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using Content.Shared.COGR.Components;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRSemanticReplicaSystem
{
    private readonly Dictionary<SemanticReplicaOwner, SemanticReplicaScope> _activeScopes = new();
    private ConnectionId? _scopeConnection;
    private bool _scopeRefreshRequired = true;

    // Kept as the initialization hook used by the main partial. COGR controlled-body
    // ComponentStartup/ComponentShutdown are exclusively owned by the authority coordinator.
    private void SubscribeSemanticScopeLifecycle()
    {
    }

    public void NotifyControlledBodyMembershipChanged()
    {
        _scopeRefreshRequired = true;
    }

    private void ClearSemanticScopeCache()
    {
        _activeScopes.Clear();
        _scopeConnection = null;
        _scopeRefreshRequired = true;
        _pendingDirtyScopes.Clear();
        _pendingDirtyOwners.Clear();

        if (EntityManager.TrySystem<COGRRegionalPerceptionRouterSystem>(out var regionalRouter))
            regionalRouter.SynchronizeSemanticScopes(Array.Empty<SemanticReplicaScope>());
    }

    private void RefreshSemanticScopesIfNeeded(ConnectionId connectionId)
    {
        if (!_scopeConnection.HasValue || _scopeConnection.Value != connectionId)
        {
            _scopeConnection = connectionId;
            _scopeRefreshRequired = true;
        }

        if (!_scopeRefreshRequired)
            return;

        _activeScopes.Clear();
        var bodyIndex = EntityManager.System<COGRBodyBindingIndexSystem>();
        var visitedAgents = new HashSet<AgentId>();
        foreach (var uid in bodyIndex.ControlledEntities)
        {
            if (!TryComp<COGRControlledComponent>(uid, out var controlled) ||
                !controlled.IsActive ||
                controlled.AgentId == Guid.Empty ||
                controlled.BodyId == Guid.Empty)
            {
                continue;
            }

            var agentId = AgentId.FromGuid(controlled.AgentId);
            if (!visitedAgents.Add(agentId))
                continue;

            var lease = _authority.ResolveBoundLease(agentId, connectionId);
            if (!lease.HasValue)
                continue;

            var scope = new SemanticReplicaScope
            {
                ConnectionId = connectionId,
                AgentId = lease.Value.AgentId,
                BodyId = lease.Value.BodyId,
                BodyGeneration = lease.Value.Generation,
            };
            _activeScopes[scope.Owner] = scope;
        }

        var staleOwners = _replicas
            .Where(entry =>
                entry.Key.ConnectionId != connectionId ||
                !_activeScopes.TryGetValue(entry.Key, out var currentScope) ||
                entry.Value.Scope != currentScope)
            .Select(entry => entry.Key)
            .ToArray();
        foreach (var owner in staleOwners)
            _replicas.Remove(owner);

        if (EntityManager.TrySystem<COGRRegionalPerceptionRouterSystem>(out var regionalRouter))
            regionalRouter.SynchronizeSemanticScopes(_activeScopes.Values);

        foreach (var scope in _activeScopes.Values.OrderBy(scope => scope.AgentId))
        {
            if (!_replicas.TryGetValue(scope.Owner, out var state) || state.Scope != scope)
                QueueDirtyScope(scope.Owner, "authority_scope_ready");
        }

        _scopeRefreshRequired = false;
    }

    private void QueueDirtyScope(SemanticReplicaOwner owner, string reason)
    {
        if (!_pendingDirtyOwners.Add(owner))
            return;

        if (_pendingDirtyScopes.Count >= MaxPendingDirtyScopes)
        {
            _pendingDirtyOwners.Remove(owner);
            _sawmill.Warning(
                "Semantic replica dirty scope dropped: agent={0} reason={1} cause=queue_full depth={2}",
                owner.AgentId,
                reason,
                _pendingDirtyScopes.Count);
            return;
        }

        _pendingDirtyScopes.Enqueue(new PendingDirtyScope(owner, reason));
        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[AUTO] replica.dirty queued agent={0} reason={1} depth={2}",
                owner.AgentId,
                reason,
                _pendingDirtyScopes.Count);
        }
    }

    private bool TryTakeNextDirtyScope(
        out SemanticReplicaScope scope,
        out string reason)
    {
        while (_pendingDirtyScopes.Count > 0)
        {
            var pending = _pendingDirtyScopes.Dequeue();
            _pendingDirtyOwners.Remove(pending.Owner);
            if (!_activeScopes.TryGetValue(pending.Owner, out scope!))
                continue;

            reason = pending.Reason;
            return true;
        }

        scope = default!;
        reason = string.Empty;
        return false;
    }
}
