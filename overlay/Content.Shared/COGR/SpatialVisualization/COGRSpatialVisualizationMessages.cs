using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared.COGR.SpatialVisualization;

/// <summary>Client-to-server request to observe one exact Coggent's privileged spatial diagnostics.</summary>
[Serializable, NetSerializable]
public sealed class RequestCOGRSpatialVisualizationMessage : EntityEventArgs
{
    public bool Enabled;
    public string AgentId = string.Empty;
}

/// <summary>One resident COGR-owned spatial belief projected into Station map coordinates solely for admin visualization.</summary>
[Serializable, NetSerializable]
public sealed class COGRSpatialVisualizationTarget
{
    public string AgentId = string.Empty;
    public string TargetId = string.Empty;
    public ulong TargetRevision;
    public bool IsRichlyMaintained;
    public bool IsFocal;
    public MapCoordinates BodyOrigin;
    public MapCoordinates Belief;
    public bool HasActual;
    public MapCoordinates Actual;

    // Realization self-checks are privileged diagnostics only. Expected and realized tile distances must agree because
    // rotation preserves vector magnitude; disagreement isolates adapter realization before client rendering is considered.
    public double BeliefLocalX;
    public double BeliefLocalY;
    public double BeliefExpectedDistanceTiles;
    public double BeliefRealizedDistanceTiles;

    // Calibration diagnostics are privileged observations only. They never flow back into COGR cognition.
    public bool HasPerceivedLocalRange;
    public double PerceivedLocalRange;
    public ulong PerceivedSampleTick;
    public ulong PerceivedSampleAgeTicks;
    public double BeliefVectorMagnitudeLocalUnits;
    public bool HasActualDistanceTiles;
    public double ActualDistanceTiles;
    public bool HasActualDistanceCalibratedLocalUnits;
    public double ActualDistanceCalibratedLocalUnits;
}

/// <summary>One transient remembered-route polyline in authoritative map coordinates.</summary>
[Serializable, NetSerializable]
public sealed class COGRSpatialVisualizationPath
{
    public ulong Sequence;
    public MapCoordinates[] Points = [];
}

/// <summary>Server-to-admin-client spatial debug frame for one exact Coggent.</summary>
[Serializable, NetSerializable]
public sealed class COGRSpatialVisualizationMessage : EntityEventArgs
{
    public string AgentId = string.Empty;
    public int ResidentTargetCount;
    public int RichlyMaintainedTargetCount;
    public int UnprojectableResidentTargetCount;
    public COGRSpatialVisualizationTarget[] Targets = [];
    public COGRSpatialVisualizationPath[] Paths = [];
}
