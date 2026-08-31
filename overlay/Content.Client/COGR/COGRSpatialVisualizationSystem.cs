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
    private static readonly TimeSpan TargetLifetime = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan PathLifetime = TimeSpan.FromSeconds(2.0);

    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<string, TimedTarget> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, TimedPath> _paths = [];
    private string? _trackedAgentId;

    public bool Enabled => _trackedAgentId is not null;
    public string? TrackedAgentId => _trackedAgentId;

    internal Dictionary<string, TimedTarget>.ValueCollection Targets => _targets.Values;
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

    /// <summary>Begins observing one exact Coggent. Switching agents first retires the previous observer scope.</summary>
    public void TrackAgent(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (!Guid.TryParse(agentId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("COGR spatial visualization requires an assigned agent UUID.", nameof(agentId));

        var canonical = parsed.ToString("D");
        if (string.Equals(_trackedAgentId, canonical, StringComparison.Ordinal))
            return;

        if (_trackedAgentId is not null)
        {
            RaiseNetworkEvent(new RequestCOGRSpatialVisualizationMessage
            {
                Enabled = false,
                AgentId = _trackedAgentId,
            });
        }

        _trackedAgentId = canonical;
        Clear();
        if (!_overlayManager.HasOverlay<COGRSpatialVisualizationOverlay>())
            _overlayManager.AddOverlay(new COGRSpatialVisualizationOverlay(this));

        RaiseNetworkEvent(new RequestCOGRSpatialVisualizationMessage
        {
            Enabled = true,
            AgentId = canonical,
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
            });
        }

        _trackedAgentId = null;
        _overlayManager.RemoveOverlay<COGRSpatialVisualizationOverlay>();
        Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!Enabled)
            return;

        // Lifetimes are disconnect/failure containment only. Normal deletion is authoritative full-frame reconciliation in
        // OnVisualizationMessage, so a stationary belief marker remains visible as long as Runtime continues reporting it.
        var now = _timing.RealTime;
        foreach (var key in _targets.Where(pair => pair.Value.ExpiresAt <= now).Select(static pair => pair.Key).ToArray())
            _targets.Remove(key);
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

        var now = _timing.RealTime;
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
            _targets[key] = new TimedTarget(target, now + TargetLifetime);
        }

        foreach (var key in _targets.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
            _targets.Remove(key);

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
    }

    internal sealed record TimedTarget(COGRSpatialVisualizationTarget Target, TimeSpan ExpiresAt);
    internal sealed record TimedPath(MapCoordinates[] Points, TimeSpan ExpiresAt);
}

/// <summary>World-space renderer for cognition-owned belief positions and remembered-route diagnostics.</summary>
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

        // Blue is deliberately reserved for COGR's own reported spatial belief. No authoritative target location is drawn
        // in this normal acceptance-testing view, so a cognitive spatial error remains visible instead of being repaired or
        // visually conflated with Station truth.
        foreach (var timed in _system.Targets)
        {
            var target = timed.Target;
            if (target.Belief.MapId != args.MapId)
                continue;
            DrawCross(handle, target.Belief.Position, Color.Blue);
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
