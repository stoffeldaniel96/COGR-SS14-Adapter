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

    // Signed realization self-checks are privileged diagnostics only. They expose each coordinate-space transition so
    // sign inversions, axis swaps, origin errors, and scale errors can be distinguished without feeding Station truth back
    // into cognition or action selection.
    public double BeliefLocalX;
    public double BeliefLocalY;
    public double BeliefExpectedNativeX;
    public double BeliefExpectedNativeY;
    public double BodyWorldRotationRadians;
    public double BeliefExpectedWorldDeltaX;
    public double BeliefExpectedWorldDeltaY;
    public double BeliefRealizedWorldDeltaX;
    public double BeliefRealizedWorldDeltaY;
    public double BeliefExpectedDistanceTiles;
    public double BeliefRealizedDistanceTiles;

    // Perception-side samples are the exact signed adapter-local components emitted as spatial evidence at one observation
    // tick. Sample age is explicit because current Station truth may legitimately differ after either body moves.
    public bool HasPerceivedLocalRange;
    public double PerceivedLocalRange;
    public bool HasPerceivedLocalVector;
    public double PerceivedLocalX;
    public double PerceivedLocalY;
    public ulong PerceivedSampleTick;
    public ulong PerceivedSampleAgeTicks;

    public double BeliefVectorMagnitudeLocalUnits;
    public bool HasActualDistanceTiles;
    public double ActualDistanceTiles;
    public bool HasActualDistanceCalibratedLocalUnits;
    public double ActualDistanceCalibratedLocalUnits;
    public bool HasActualWorldDelta;
    public double ActualWorldDeltaX;
    public double ActualWorldDeltaY;
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
