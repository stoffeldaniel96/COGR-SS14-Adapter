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

/// <summary>One COGR-owned spatial belief projected into Station map coordinates solely for admin visualization.</summary>
[Serializable, NetSerializable]
public sealed class COGRSpatialVisualizationTarget
{
    public string AgentId = string.Empty;
    public string TargetId = string.Empty;
    public ulong TargetRevision;
    public bool IsTracked;
    public MapCoordinates Belief;
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
    public COGRSpatialVisualizationTarget[] Targets = [];
    public COGRSpatialVisualizationPath[] Paths = [];
}
