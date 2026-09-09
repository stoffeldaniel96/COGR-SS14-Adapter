using System.Collections.Concurrent;
using Content.Shared.COGR.SpatialVisualization;
using Robust.Shared.Network;

namespace Content.Client.COGR;

/// <summary>
/// Receives admin-only latest-state visualization packets outside MsgEntity's simulation-tick queue, then transfers them
/// onto the ordinary client entity-system update boundary before touching marker state.
/// </summary>
public sealed class COGRSpatialVisualizationLatestTransportSystem : EntitySystem
{
    [Dependency] private IClientNetManager _netManager = default!;

    private readonly ConcurrentQueue<COGRSpatialVisualizationMessage> _pending = new();
    private COGRSpatialVisualizationSystem _visualization = default!;

    public override void Initialize()
    {
        base.Initialize();
        _visualization = EntityManager.System<COGRSpatialVisualizationSystem>();
        _netManager.RegisterNetMessage<MsgCOGRSpatialVisualizationLatest>(
            OnLatestSnapshot,
            NetMessageAccept.Client);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        COGRSpatialVisualizationMessage? newest = null;
        while (_pending.TryDequeue(out var candidate))
        {
            if (newest is null
                || candidate.VisualizationStreamId != newest.VisualizationStreamId
                || candidate.VisualizationFrameSequence > newest.VisualizationFrameSequence)
            {
                newest = candidate;
            }
        }

        if (newest is not null)
            _visualization.AcceptLatestTransportSnapshot(newest);
    }

    private void OnLatestSnapshot(MsgCOGRSpatialVisualizationLatest message)
    {
        if (message.Snapshot is not null)
            _pending.Enqueue(message.Snapshot);
    }
}

public sealed partial class COGRSpatialVisualizationSystem
{
    internal event Action<COGRSpatialVisualizationMessage>? LatestTransportSnapshotApplied;

    internal void AcceptLatestTransportSnapshot(COGRSpatialVisualizationMessage message)
    {
        var previousStream = _visualizationStreamId;
        var previousSequence = _latestVisualizationFrameSequence;
        OnVisualizationMessage(message);

        if (_visualizationStreamId == message.VisualizationStreamId
            && _latestVisualizationFrameSequence == message.VisualizationFrameSequence
            && (previousStream != _visualizationStreamId || previousSequence != _latestVisualizationFrameSequence))
        {
            LatestTransportSnapshotApplied?.Invoke(message);
        }
    }
}