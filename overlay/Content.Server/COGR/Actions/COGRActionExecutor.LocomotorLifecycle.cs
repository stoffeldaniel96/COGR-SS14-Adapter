using COGR.Core.Identifiers;

namespace Content.Server.COGR.Actions;

public sealed partial class COGRActionExecutor
{
    /// <summary>
    /// Removes executor-owned native steering state for every locomotor realization associated with one body.
    /// This is physical actuator cleanup only; it does not choose, maintain, or reinterpret any cognitive goal.
    /// </summary>
    private void CleanupLocomotorSteeringForBody(BodyId bodyId)
    {
        var directionalProposals = _directionalSteering
            .Where(pair => pair.Value.BodyId == bodyId)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (var proposalId in directionalProposals)
            CleanupDirectionalSteering(proposalId);

        CleanupAllProjectedObjectiveSteeringForBody(bodyId);
    }
}
