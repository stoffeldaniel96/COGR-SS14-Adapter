using Content.Shared.COGR.SpatialVisualization;
using Robust.Shared.Network;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Registers the admin-only latest-state spatial visualization transport before clients complete their serialization
/// handshake. The message is send-only from Station and is never accepted as client authority.
/// </summary>
public sealed class COGRSpatialVisualizationLatestTransportRegistrationSystem : EntitySystem
{
    [Dependency] private IServerNetManager _netManager = default!;

    public override void Initialize()
    {
        base.Initialize();
        _netManager.RegisterNetMessage<MsgCOGRSpatialVisualizationLatest>(accept: NetMessageAccept.Client);
    }
}

public sealed partial class COGRSpatialVisualizationSystem
{
    [Dependency] private IServerNetManager _spatialVisualizationNetManager = default!;

    /// <summary>
    /// Exact overload intentionally replaces EntitySystem.RaiseNetworkEvent for spatial visualization snapshots only.
    /// The request/control path remains an ordinary authenticated ECS event; only the replaceable server-to-client admin
    /// snapshot bypasses MsgEntity's reliable-ordered/source-tick dispatch semantics.
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