using Content.Shared.COGR.SpatialVisualization;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Content.Client.COGR;

/// <summary>
/// Admin-only transport observer for the spatial visualization stream. This system does not own marker state and cannot
/// affect cognition or visualization acceptance. It distinguishes a stalled client update loop from delayed visualization
/// event delivery by reporting bounded receive gaps independently of marker installation.
/// </summary>
public sealed class COGRSpatialVisualizationTransportDiagnosticSystem : EntitySystem
{
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StallReportInterval = TimeSpan.FromMilliseconds(500);

    [Dependency] private IGameTiming _timing = default!;
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
        SubscribeNetworkEvent<COGRSpatialVisualizationMessage>(OnVisualizationMessage);
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
        _sawmill.Warning(
            "visualization receive stall ageMs={0:F1} lastFrame={1} stream={2:N} clientTick={3} resident={4} projected={5} paths={6}",
            age.TotalMilliseconds,
            _lastFrameSequence,
            _streamId,
            (ulong)_timing.CurTick.Value,
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
            _sawmill.Warning(
                "visualization receive recovered gapMs={0:F1} frame={1} previousFrame={2} sequenceDelta={3} stream={4:N} clientTick={5} resident={6} projected={7} paths={8}",
                gap.TotalMilliseconds,
                message.VisualizationFrameSequence,
                previousSequence,
                sequenceDelta,
                message.VisualizationStreamId,
                (ulong)_timing.CurTick.Value,
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
