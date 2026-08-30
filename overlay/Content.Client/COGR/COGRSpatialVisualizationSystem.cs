using System.Linq;
using System.Numerics;
using Content.Shared.COGR.SpatialVisualization;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client.COGR;

/// <summary>Client-only transient visualization of privileged COGR spatial debug data.</summary>
public sealed partial class COGRSpatialVisualizationSystem : EntitySystem
{
    private static readonly TimeSpan TargetLifetime = TimeSpan.FromSeconds(1.75);
    private static readonly TimeSpan PointerLifetime = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan PathLifetime = TimeSpan.FromSeconds(2.0);

    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<string, TimedTarget> _targets = new(StringComparer.Ordinal);
    private readonly List<TimedPointer> _pointers = [];
    private readonly Dictionary<ulong, TimedPath> _paths = [];
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;

            _enabled = value;
            if (_enabled)
            {
                if (!_overlayManager.HasOverlay<COGRSpatialVisualizationOverlay>())
                    _overlayManager.AddOverlay(new COGRSpatialVisualizationOverlay(this));
            }
            else
            {
                _overlayManager.RemoveOverlay<COGRSpatialVisualizationOverlay>();
                Clear();
            }

            RaiseNetworkEvent(new RequestCOGRSpatialVisualizationMessage
            {
                Enabled = _enabled,
            });
        }
    }

    internal Dictionary<string, TimedTarget>.ValueCollection Targets => _targets.Values;
    internal List<TimedPointer> Pointers => _pointers;
    internal Dictionary<ulong, TimedPath>.ValueCollection Paths => _paths.Values;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<COGRSpatialVisualizationMessage>(OnVisualizationMessage);
    }

    public override void Shutdown()
    {
        if (_enabled)
        {
            RaiseNetworkEvent(new RequestCOGRSpatialVisualizationMessage
            {
                Enabled = false,
            });
        }

        _enabled = false;
        _overlayManager.RemoveOverlay<COGRSpatialVisualizationOverlay>();
        Clear();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_enabled)
            return;

        var now = _timing.RealTime;
        foreach (var key in _targets.Where(pair => pair.Value.ExpiresAt <= now).Select(static pair => pair.Key).ToArray())
            _targets.Remove(key);
        foreach (var sequence in _paths.Where(pair => pair.Value.ExpiresAt <= now).Select(static pair => pair.Key).ToArray())
            _paths.Remove(sequence);
        _pointers.RemoveAll(pointer => pointer.ExpiresAt <= now);
    }

    private void OnVisualizationMessage(COGRSpatialVisualizationMessage message)
    {
        if (!_enabled)
            return;

        var now = _timing.RealTime;
        foreach (var target in message.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.AgentId) || string.IsNullOrWhiteSpace(target.TargetId))
                continue;

            var key = string.Concat(target.AgentId, ":", target.TargetId);
            _targets[key] = new TimedTarget(target, now + TargetLifetime);
            if (target.PulsePointer)
                _pointers.Add(new TimedPointer(target.Belief, now + PointerLifetime));
        }

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
        _pointers.Clear();
        _paths.Clear();
    }

    internal sealed record TimedTarget(COGRSpatialVisualizationTarget Target, TimeSpan ExpiresAt);
    internal sealed record TimedPointer(MapCoordinates Coordinates, TimeSpan ExpiresAt);
    internal sealed record TimedPath(MapCoordinates[] Points, TimeSpan ExpiresAt);
}

/// <summary>World-space renderer for COGR belief-vs-reality and remembered-path diagnostics.</summary>
public sealed class COGRSpatialVisualizationOverlay : Overlay
{
    private const float EndpointMarkerRadius = 0.07f;
    private static readonly Vector2 PointerLeft = new(-0.18f, 0.34f);
    private static readonly Vector2 PointerRight = new(0.18f, 0.34f);
    private static readonly Vector2 PointerStem = new(0f, 0.52f);

    private readonly COGRSpatialVisualizationSystem _system;

    public COGRSpatialVisualizationOverlay(COGRSpatialVisualizationSystem system)
    {
        _system = system;
    }

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;

        foreach (var timed in _system.Targets)
        {
            var target = timed.Target;
            if (!target.IsTracked
                || target.Body.MapId != args.MapId
                || target.Belief.MapId != args.MapId)
            {
                continue;
            }

            handle.DrawLine(target.Body.Position, target.Belief.Position, Color.Yellow);
            DrawCross(handle, target.Belief.Position, Color.Yellow);

            if (target.HasActual && target.Actual.MapId == args.MapId)
            {
                handle.DrawLine(target.Body.Position, target.Actual.Position, Color.Red);
                DrawCross(handle, target.Actual.Position, Color.Red);
            }
        }

        foreach (var pointer in _system.Pointers)
        {
            if (pointer.Coordinates.MapId != args.MapId)
                continue;
            DrawPointer(handle, pointer.Coordinates.Position);
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

    private static void DrawPointer(DrawingHandleWorld handle, Vector2 point)
    {
        handle.DrawLine(point + PointerLeft, point, Color.Yellow);
        handle.DrawLine(point + PointerRight, point, Color.Yellow);
        handle.DrawLine(point + PointerStem, point + new Vector2(0f, 0.12f), Color.Yellow);
    }
}
