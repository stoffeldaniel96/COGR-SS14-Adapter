using System.IO;
using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared.COGR.SpatialVisualization;

/// <summary>
/// Admin-only latest-state transport for COGR spatial visualization snapshots.
///
/// Spatial visualization is replaceable diagnostic state, not simulation history. It therefore must not inherit
/// MsgEntity's reliable-ordered/source-tick dispatch semantics: a lost diagnostic frame is harmless because a newer full
/// snapshot supersedes it, while head-of-line blocking can make an otherwise-correct egocentric diagnostic appear stale.
/// The payload's process stream id and monotonic visualization frame sequence remain the authority for client currency.
/// </summary>
public sealed class MsgCOGRSpatialVisualizationLatest : NetMessage
{
    public COGRSpatialVisualizationMessage Snapshot { get; set; } = new();

    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    // Deliberately not ordered or reliable. The server emits complete replacement snapshots and the client already rejects
    // stale/non-advancing visualization frame sequences. This keeps admin diagnostics independent of simulation-tick ECS
    // event ordering and of the per-player ordered encryption/send channel.
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.Unreliable;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        var length = buffer.ReadVariableInt32();
        using var stream = RobustMemoryManager.GetMemoryStream(length);
        buffer.ReadAlignedMemory(stream, length);
        Snapshot = serializer.Deserialize<COGRSpatialVisualizationMessage>(stream);
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        using var stream = new MemoryStream();
        serializer.Serialize(stream, Snapshot);
        buffer.WriteVariableInt32(checked((int)stream.Length));
        buffer.Write(stream.AsSpan());
    }
}