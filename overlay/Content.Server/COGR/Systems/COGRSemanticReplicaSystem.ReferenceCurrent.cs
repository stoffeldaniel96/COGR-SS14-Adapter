using COGR.Core.Actions;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Time;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRSemanticReplicaSystem
{
    /// <summary>
    /// Returns whether one opaque environment reference remains present in the exact current actor-relative
    /// semantic replica owned by an accepted action. This is retained replica membership, not a fresh visibility test.
    /// </summary>
    public bool IsReferenceCurrentlyObserved(ActionAttempt attempt, EnvironmentRef environmentReference)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!environmentReference.IsAssigned ||
            !attempt.AuthorityLease.IsValid ||
            attempt.AuthorityLease.AgentId != attempt.AgentId ||
            attempt.AuthorityLease.BodyId != attempt.BodyId)
        {
            return false;
        }

        foreach (var (owner, state) in _replicas)
        {
            if (owner.AgentId != attempt.AgentId ||
                owner.ConnectionId != attempt.AuthorityLease.ConnectionId)
            {
                continue;
            }

            var scope = state.Scope;
            return scope.AgentId == attempt.AgentId &&
                   scope.ConnectionId == attempt.AuthorityLease.ConnectionId &&
                   scope.BodyId == attempt.BodyId &&
                   scope.BodyGeneration == attempt.AuthorityLease.Generation &&
                   state.Observations.ContainsKey(environmentReference);
        }

        return false;
    }

    /// <summary>
    /// Tests whether an already-grounded opaque reference is physically visible to the exact current action body now.
    /// Resolution remains entirely inside the Station adapter and is used only to decide whether an existing physical
    /// coupling may continue; hidden coordinates are never returned to cognition and loss does not perform reacquisition.
    /// </summary>
    public bool IsReferenceCurrentlyVisuallyAvailable(
        ActionAttempt attempt,
        EnvironmentRef environmentReference)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!environmentReference.IsAssigned ||
            !attempt.AuthorityLease.IsValid ||
            attempt.AuthorityLease.AgentId != attempt.AgentId ||
            attempt.AuthorityLease.BodyId != attempt.BodyId)
        {
            return false;
        }

        var connectionId = attempt.AuthorityLease.ConnectionId;
        var currentLease = _authority.ResolveBoundLease(attempt.AgentId, connectionId);
        if (!currentLease.HasValue ||
            currentLease.Value.BodyId != attempt.BodyId ||
            currentLease.Value.Generation != attempt.AuthorityLease.Generation)
        {
            return false;
        }

        var body = _authority.ResolveBoundBody(
            attempt.AgentId,
            attempt.BodyId,
            connectionId,
            attempt.AuthorityLease.Generation);
        if (!body.HasValue)
            return false;

        var registry = _adapter.ReferenceRegistry;
        if (registry is null)
            return false;

        var resolved = registry.TryResolve(
            environmentReference,
            new EnvironmentReferenceResolutionContext
            {
                ConnectionId = connectionId,
                CurrentTick = new SimTick((ulong)_timing.CurTick.Value),
                BodyId = attempt.BodyId,
                BodyGeneration = attempt.AuthorityLease.Generation,
            });
        if (!resolved.HasValue)
            return false;

        return _perception.IsEntityCurrentlyVisuallyAvailable(
            body.Value,
            resolved.Value,
            COGRSpatialPolicy.DefaultVisualHorizon);
    }
}
