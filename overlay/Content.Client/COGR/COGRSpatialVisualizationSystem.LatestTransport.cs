using System.Collections.Concurrent;
using Content.Shared.COGR.SpatialVisualization;

namespace Content.Client.COGR;

/// <summary>
/// Receives admin-only latest-state visualization packets outside MsgEntity's simulation-tick queue, then transfers them
/// onto the ordinary client entity-system update boundary before touching marker state.
///
/// Net-message registration is intentionally owned by the content startup entrypoint, before Robust's network string-table
/// handshake. This system only drains the startup-registered transport inbox.
/// </summary>
public sealed class COGRSpatialVisualizationLatestTransportSystem : EntitySystem
{
    private COGRSpatialVisualizationSystem _visualization = default!;

    public override void Initialize()
    {
        base.Initialize();
        _visualization = EntityManager.System<COGRSpatialVisualizationSystem>();
        COGRSpatialVisualizationLatestTransportInbox.Clear();
    }

    public override void Shutdown()
    {
        COGRSpatialVisualizationLatestTransportInbox.Clear();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var newest = COGRSpatialVisualizationLatestTransportInbox.DrainNewest();
        if (newest is not null)
            _visualization.AcceptLatestTransportSnapshot(newest);
    }
}

/// <summary>
/// Startup-registered process inbox for the admin-only spatial visualization transport.
/// The Robust net callback may run before the visualization entity system is available, so registration cannot directly
/// depend on that system. Full snapshots are complete replacements; draining keeps only the newest frame per update.
/// </summary>
internal static class COGRSpatialVisualizationLatestTransportInbox
{
    private static readonly ConcurrentQueue<COGRSpatialVisualizationMessage> Pending = new();

    internal static void Receive(MsgCOGRSpatialVisualizationLatest message)
    {
        if (message.Snapshot is not null)
            Pending.Enqueue(message.Snapshot);
    }

    internal static COGRSpatialVisualizationMessage? DrainNewest()
    {
        COGRSpatialVisualizationMessage? newest = null;
        while (Pending.TryDequeue(out var candidate))
        {
            if (newest is null
                || candidate.VisualizationStreamId != newest.VisualizationStreamId
                || candidate.VisualizationFrameSequence > newest.VisualizationFrameSequence)
            {
                newest = candidate;
            }
        }

        return newest;
    }

    internal static void Clear()
    {
        while (Pending.TryDequeue(out _))
        {
        }
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
