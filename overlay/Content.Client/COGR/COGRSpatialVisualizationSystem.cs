using System.Linq;
using System.Numerics;
using Content.Shared.COGR.SpatialVisualization;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client.COGR;

/// <summary>Client-only admin visualization of one selected Coggent's cognition-owned spatial beliefs.</summary>
public sealed partial class COGRSpatialVisualizationSystem : EntitySystem
{
    private static readonly TimeSpan PathLifetime = TimeSpan.FromSeconds(2.0);

    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<string, COGRSpatialVisualizationTarget> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, TimedPath> _paths = [];
    private string? _trackedAgentId;
    private string? _trackedTargetId;
    private int _residentTargetCount;
    private int _richlyMaintainedTargetCount;
    private int _unprojectableResidentTargetCount;

    public bool Enabled => _trackedAgentId is not null;
    public string? TrackedAgentId => _trackedAgentId;
    public string? TrackedTargetId => _trackedTargetId;
    public int ResidentTargetCount => _residentTargetCount;
    public int RichlyMaintainedTargetCount => _richlyMaintainedTargetCount;
    public int UnprojectableResidentTargetCount => _unprojectableResidentTargetCount;
    public int ProjectedResidentTargetCount => _targets.Count;

    internal Dictionary<string, COGRSpatialVisualizationTarget>.ValueCollection Targets => _targets.Values;
    internal Dictionary<ulong, TimedPath>.ValueCollection Paths => _paths.Values;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<COGRSpatialVisualizationMessage>(OnVisualizationMessage);
    }

    public override void Shutdown()
    {
        StopTracking();
        base.Shutdown();
    }

    /// <summary>
    /// Begins observing one exact Coggent. An optional target identity scopes server-side diagnostic telemetry only; the
    /// client still receives and renders the complete successful Runtime visualization frame.
    /// </summary>
    public void TrackAgent(string agentId, string? targetId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (!Guid.TryParse(agentId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("COGR spatial visualization requires an assigned agent UUID.", nameof(agentId));

        var canonical = parsed.ToString("D");
        var selectedTargetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId.Trim();
        var sameAgent = string.Equals(_trackedAgentId, canonical, StringComparison.Ordinal);
        if (sameAgent && string.Equals(_trackedTargetId, selectedTargetId, StringComparison.Ordinal))
            return;

        if (_trackedAgentId is not null && !sameAgent)
        {
            RaiseNetworkEvent(new RequestCOGRSpatialVisualizationMessage
            {
                Enabled = false,
                AgentId = _trackedAgentId,
                TargetId = _trackedTargetId ?? string.Empty,
            });
        }

        _trackedAgentId = canonical;
        _trackedTargetId = selectedTargetId;
        if (!sameAgent)
        {
            Clear();
            if (!_overlayManager.HasOverlay<COGRSpatialVisualizationOverlay>())
                _overlayManager.AddOverlay(new COGRSpatialVisualizationOverlay(this));
        }

        RaiseNetworkEvent(new RequestCOGRSpatialVisualizationMessage
        {
            Enabled = true,
            AgentId = canonical,
            TargetId = selectedTargetId ?? string.Empty,
        });
    }

    /// <summary>Stops the current observer scope and clears all transient debug render state.</summary>
    public void StopTracking()
    {
        if (_trackedAgentId is { } agentId)
        {
            RaiseNetworkEvent(new RequestCOGRSpatialVisualizationMessage
            {
                Enabled = false,
                AgentId = agentId,
                TargetId = _trackedTargetId ?? string.Empty,
            });
        }

        _trackedAgentId = null;
        _trackedTargetId = null;
        _overlayManager.RemoveOverlay<COGRSpatialVisualizationOverlay>();
        Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!Enabled)
            return;

        // Remembered-route paths are transient diagnostic events. Belief targets are retained until a successful Runtime
        // full frame authoritatively omits them; a local transport timeout must never masquerade as cognitive retirement.
        var now = _timing.RealTime;
        foreach (var sequence in _paths.Where(pair => pair.Value.ExpiresAt <= now).Select(static pair => pair.Key).ToArray())
            _paths.Remove(sequence);
    }

    private void OnVisualizationMessage(COGRSpatialVisualizationMessage message)
    {
        if (_trackedAgentId is null
            || !string.Equals(message.AgentId, _trackedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _residentTargetCount = Math.Max(0, message.ResidentTargetCount);
        _richlyMaintainedTargetCount = Math.Clamp(
            message.RichlyMaintainedTargetCount,
            0,
            _residentTargetCount);
        _unprojectableResidentTargetCount = Math.Clamp(
            message.UnprojectableResidentTargetCount,
            0,
            _residentTargetCount);

        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in message.Targets)
        {
            if (!string.Equals(target.AgentId, _trackedAgentId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(target.TargetId))
            {
                continue;
            }

            var key = string.Concat(target.AgentId, ":", target.TargetId);
            currentKeys.Add(key);
            _targets[key] = target;
        }

        foreach (var key in _targets.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
            _targets.Remove(key);

        var now = _timing.RealTime;
        foreach (var path in message.Paths)
        {
            if (path.Points.Length < 2)
                continue;
            _paths[path.Sequence] = new TimedPath(path.Points, now + PathLifetime);
        }
    }

    private void Clear()
    {
        _targets.Clear();
        _paths.Clear();
        _residentTargetCount = 0;
        _richlyMaintainedTargetCount = 0;
        _unprojectableResidentTargetCount = 0;
    }

    internal sealed record TimedPath(MapCoordinates[] Points, TimeSpan ExpiresAt);
}

/// <summary>World-space renderer for cognition-owned resident belief positions and remembered-route diagnostics.</summary>
public sealed class COGRSpatialVisualizationOverlay : Overlay
{
    private const float EndpointMarkerRadius = 0.09f;
    private readonly COGRSpatialVisualizationSystem _system;

    public COGRSpatialVisualizationOverlay(COGRSpatialVisualizationSystem system)
    {
        _system = system;
    }

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;

        // Blue means a current resident belief-target representation. Red is the Runtime's explicit current perceptual-
        // attention focus. Cyan is the privileged Station-resolved Coggent body origin and yellow is the privileged current
        // Station referent position. Cyan/yellow exist solely to audit diagnostic coordinate realization and never feed back
        // into cognition or action selection.
        foreach (var target in _system.Targets)
        {
            if (target.BodyOrigin.MapId == args.MapId)
                DrawCross(handle, target.BodyOrigin.Position, Color.Cyan);
            if (target.HasActual && target.Actual.MapId == args.MapId)
                DrawCross(handle, target.Actual.Position, Color.Yellow);
            if (target.Belief.MapId == args.MapId)
                DrawCross(handle, target.Belief.Position, target.IsFocal ? Color.Red : Color.Blue);
        }

        foreach (var path in _system.Paths)
        {
            for (var index = 1; index < path.Points.Length; index++)
            {
                var previous = path.Points[index - 1];
                var current = path.Points[index];
                if (previous.MapId != args.MapId || current.MapId != args.MapId)
                    continue;
                handle.DrawLine(previous.Position, current.Position, Color.Green);
            }
        }
    }

    private static void DrawCross(DrawingHandleWorld handle, Vector2 center, Color color)
    {
        var horizontal = new Vector2(EndpointMarkerRadius, 0f);
        var vertical = new Vector2(0f, EndpointMarkerRadius);
        handle.DrawLine(center - horizontal, center + horizontal, color);
        handle.DrawLine(center - vertical, center + vertical, color);
    }
}
