using Content.Shared.COGR.SpatialVisualization;
using Robust.Client.Timing;
using Robust.Shared.Log;

namespace Content.Client.COGR;

/// <summary>
/// Admin-only transport observer for the spatial visualization stream. This system does not own marker state and cannot
/// affect cognition or visualization acceptance. It reports bounded gaps in successfully applied latest-state snapshots and
/// retains client authoritative-tick diagnostics while the old MsgEntity path is being retired.
/// </summary>
public sealed class COGRSpatialVisualizationTransportDiagnosticSystem : EntitySystem
{
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StallReportInterval = TimeSpan.FromMilliseconds(500);

    [Dependency] private IClientGameTiming _timing = default!;
    [Dependency] private ILogManager _logManager = default!;

    private COGRSpatialVisualizationSystem _visualization = default!;
    private ISawmill _sawmill = default!;
    private TimeSpan? _lastArrival;
    private TimeSpan _nextStallReport;
    private Guid _streamId;
    private ulong _lastFrameSequence;
    private int _lastResidentTargetCount;
    private int _lastProjectedTargetCount;
    private int _lastPathCount;

    public override void Initialize()
    {
        base.Initialize();
        _visualization = EntityManager.System<COGRSpatialVisualizationSystem>();
        _sawmill = _logManager.GetSawmill("cogr.spatialtransport");
        _visualization.LatestTransportSnapshotApplied += OnVisualizationMessage;
    }

    public override void Shutdown()
    {
        _visualization.LatestTransportSnapshotApplied -= OnVisualizationMessage;
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_visualization.Enabled || !_lastArrival.HasValue)
            return;

        var now = _timing.RealTime;
        var age = now - _lastArrival.Value;
        if (age < StallThreshold || now < _nextStallReport)
            return;

        _nextStallReport = now + StallReportInterval;
        var currentTick = _timing.CurTick;
        var lastRealTick = _timing.LastRealTick;
        _sawmill.Warning(
            "visualization latest-state stall ageMs={0:F1} lastFrame={1} stream={2:N} curTick={3} lastRealTick={4} authoritativeLagTicks={5} lastProcessedTick={6} resident={7} projected={8} paths={9}",
            age.TotalMilliseconds,
            _lastFrameSequence,
            _streamId,
            (ulong)currentTick.Value,
            (ulong)lastRealTick.Value,
            currentTick.Value >= lastRealTick.Value ? currentTick.Value - lastRealTick.Value : 0,
            (ulong)_timing.LastProcessedTick.Value,
            _lastResidentTargetCount,
            _lastProjectedTargetCount,
            _lastPathCount);
    }

    private void OnVisualizationMessage(COGRSpatialVisualizationMessage message)
    {
        if (!_visualization.Enabled
            || _visualization.TrackedAgentId is null
            || !string.Equals(message.AgentId, _visualization.TrackedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = _timing.RealTime;
        var streamChanged = _streamId != message.VisualizationStreamId;
        var previousSequence = streamChanged ? 0UL : _lastFrameSequence;
        var gap = _lastArrival.HasValue ? now - _lastArrival.Value : TimeSpan.Zero;

        if (_lastArrival.HasValue && gap >= StallThreshold)
        {
            var sequenceDelta = previousSequence == 0 || message.VisualizationFrameSequence <= previousSequence
                ? 0UL
                : message.VisualizationFrameSequence - previousSequence;
            var currentTick = _timing.CurTick;
            var lastRealTick = _timing.LastRealTick;
            _sawmill.Warning(
                "visualization latest-state recovered gapMs={0:F1} frame={1} previousFrame={2} sequenceDelta={3} stream={4:N} curTick={5} lastRealTick={6} authoritativeLagTicks={7} lastProcessedTick={8} resident={9} projected={10} paths={11}",
                gap.TotalMilliseconds,
                message.VisualizationFrameSequence,
                previousSequence,
                sequenceDelta,
                message.VisualizationStreamId,
                (ulong)currentTick.Value,
                (ulong)lastRealTick.Value,
                currentTick.Value >= lastRealTick.Value ? currentTick.Value - lastRealTick.Value : 0,
                (ulong)_timing.LastProcessedTick.Value,
                message.ResidentTargetCount,
                message.Targets.Length,
                message.Paths.Length);
        }

        _streamId = message.VisualizationStreamId;
        _lastFrameSequence = message.VisualizationFrameSequence;
        _lastResidentTargetCount = message.ResidentTargetCount;
        _lastProjectedTargetCount = message.Targets.Length;
        _lastPathCount = message.Paths.Length;
        _lastArrival = now;
        _nextStallReport = now + StallThreshold;
    }
}