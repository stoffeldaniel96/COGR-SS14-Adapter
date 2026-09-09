using Content.Shared.COGR.SpatialVisualization;
using Robust.Shared.Network;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRSpatialVisualizationSystem
{
    [Dependency] private IServerNetManager _spatialVisualizationNetManager = default!;

    /// <summary>
    /// Exact overload intentionally replaces EntitySystem.RaiseNetworkEvent for spatial visualization snapshots only.
    /// The request/control path remains an ordinary authenticated ECS event; only the replaceable server-to-client admin
    /// snapshot bypasses MsgEntity's reliable-ordered/source-tick dispatch semantics.
    ///
    /// MsgCOGRSpatialVisualizationLatest itself is registered during content startup, before Robust's network string-table
    /// handshake. This system owns only emission of already-registered snapshots.
    /// </summary>
    private void RaiseNetworkEvent(COGRSpatialVisualizationMessage message, INetChannel channel)
    {
        _spatialVisualizationNetManager.ServerSendMessage(
            new MsgCOGRSpatialVisualizationLatest
            {
                Snapshot = message,
            },
            channel);
    }
}
