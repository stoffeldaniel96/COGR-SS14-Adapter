using System.Linq;
using COGR.Core.Identifiers;
using COGR.Core.Perception;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Adapter-only, admin-observability cache for the exact normalized range emitted with perceptual observations.
/// It is enabled only while a Coggent has an active spatial-debug subscriber. Values never flow back into Runtime and
/// therefore cannot repair, bias, or otherwise influence cognition.
/// </summary>
internal static class COGRSpatialCalibrationDiagnosticCache
{
    private const int MaximumSamplesPerAgent = 4096;

    private static readonly object Gate = new();
    private static readonly HashSet<AgentId> EnabledAgents = [];
    private static readonly Dictionary<AgentId, Dictionary<EnvironmentRef, PerceivedRangeSample>> Samples = [];

    internal sealed record PerceivedRangeSample(double LocalDistance, ulong ObservedTick);

    internal static void SetEnabled(AgentId agentId, bool enabled)
    {
        if (!agentId.IsAssigned)
            return;

        lock (Gate)
        {
            if (enabled)
            {
                EnabledAgents.Add(agentId);
                return;
            }

            EnabledAgents.Remove(agentId);
            Samples.Remove(agentId);
        }
    }

    internal static void Record(
        AgentId agentId,
        EnvironmentRef environmentReference,
        double localDistance,
        ulong observedTick)
    {
        if (!agentId.IsAssigned
            || !environmentReference.IsAssigned
            || !double.IsFinite(localDistance)
            || localDistance < 0.0)
        {
            return;
        }

        lock (Gate)
        {
            if (!EnabledAgents.Contains(agentId))
                return;

            if (!Samples.TryGetValue(agentId, out var byReference))
            {
                byReference = [];
                Samples.Add(agentId, byReference);
            }

            if (!byReference.ContainsKey(environmentReference)
                && byReference.Count >= MaximumSamplesPerAgent)
            {
                var oldest = byReference
                    .OrderBy(static pair => pair.Value.ObservedTick)
                    .ThenBy(static pair => pair.Key)
                    .First();
                byReference.Remove(oldest.Key);
            }

            byReference[environmentReference] = new PerceivedRangeSample(localDistance, observedTick);
        }
    }

    internal static bool TryGet(
        AgentId agentId,
        EnvironmentRef environmentReference,
        out PerceivedRangeSample? sample)
    {
        lock (Gate)
        {
            if (Samples.TryGetValue(agentId, out var byReference)
                && byReference.TryGetValue(environmentReference, out var found))
            {
                sample = found;
                return true;
            }
        }

        sample = null;
        return false;
    }
}
