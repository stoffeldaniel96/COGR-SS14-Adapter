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
/// MsgEntity's reliable-ordered/source-tick dispatch semantics. New full snapshots may supersede older ones without waiting
/// for them in sequence; the payload's process stream id and monotonic visualization frame sequence remain the authority for
/// client currency.
/// </summary>
public sealed class MsgCOGRSpatialVisualizationLatest : NetMessage
{
    public COGRSpatialVisualizationMessage Snapshot { get; set; } = null!;

    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    // ReliableUnordered keeps complete diagnostic frames robust without entering Robust's ordered encryption/send lane.
    // Retransmitted older frames cannot roll marker state backward because the client rejects non-advancing frame sequences.
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.ReliableUnordered;

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