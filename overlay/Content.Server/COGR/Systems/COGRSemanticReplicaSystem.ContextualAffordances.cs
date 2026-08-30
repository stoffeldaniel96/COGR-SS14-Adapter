using COGR.Core.Perception;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRSemanticReplicaSystem
{
    /// <summary>
    /// Resolves one exact opaque reference only when the observer replica remains current
    /// under the query's complete connection, agent, body, and generation authority scope.
    /// </summary>
    public bool TryGetCurrentObservation(
        ContextualAffordanceQuery query,
        out Observation? observation)
    {
        ArgumentNullException.ThrowIfNull(query);
        observation = null;

        if (!_replicas.TryGetValue(
                new SemanticReplicaOwner(query.ConnectionId, query.AgentId),
                out var state) ||
            state.Scope.ConnectionId != query.ConnectionId ||
            state.Scope.AgentId != query.AgentId ||
            state.Scope.BodyId != query.BodyId ||
            state.Scope.BodyGeneration != query.BodyGeneration)
        {
            return false;
        }

        return state.Observations.TryGetValue(query.EnvironmentReference, out observation);
    }
}
