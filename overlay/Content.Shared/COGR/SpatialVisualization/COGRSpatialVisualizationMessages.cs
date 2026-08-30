using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared.COGR.SpatialVisualization;

/// <summary>Client-to-server subscription request for privileged COGR spatial cognition visualization.</summary>
[Serializable, NetSerializable]
public sealed class RequestCOGRSpatialVisualizationMessage : EntityEventArgs
{
    public bool Enabled;
}

/// <summary>One transient comparison between a Coggent belief target and its privileged authoritative referent.</summary>
[Serializable, NetSerializable]
public sealed class COGRSpatialVisualizationTarget
{
    public string AgentId = string.Empty;
    public string TargetId = string.Empty;
    public ulong TargetRevision;
    public bool IsTracked;
    public MapCoordinates Body;
    public MapCoordinates Belief;
    public bool HasActual;
    public MapCoordinates Actual;
    public bool PulsePointer;
}

/// <summary>One transient remembered-route polyline in authoritative map coordinates.</summary>
[Serializable, NetSerializable]
public sealed class COGRSpatialVisualizationPath
{
    public ulong Sequence;
    public MapCoordinates[] Points = [];
}

/// <summary>Server-to-admin-client transient spatial debug frame.</summary>
[Serializable, NetSerializable]
public sealed class COGRSpatialVisualizationMessage : EntityEventArgs
{
    public COGRSpatialVisualizationTarget[] Targets = [];
    public COGRSpatialVisualizationPath[] Paths = [];
}
