using System.Numerics;
using Content.Shared.Eye;
using Content.Shared.Interaction;
using Robust.Shared.Enums;
using Robust.Shared.GameStates;
using Robust.Shared.Map;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRBoundedPerceptionSystem
{
    /// <summary>
    /// Returns whether one concrete adapter entity is currently visually available to an observer within the requested
    /// actor-relative horizon. This is an adapter-truth visibility test only: it does not mint cognition identity, retain
    /// hidden target coordinates, or expand perception on cognition's behalf.
    /// </summary>
    public bool IsEntityCurrentlyVisuallyAvailable(
        EntityUid observer,
        EntityUid target,
        double observedRange = COGRSpatialPolicy.DefaultVisualHorizon)
    {
        if (!double.IsFinite(observedRange) || observedRange <= 0)
            throw new ArgumentOutOfRangeException(nameof(observedRange));
        if (Deleted(observer) || Deleted(target))
            return false;

        return TryGetVisualFootprintQuality(
            observer,
            target,
            observedRange,
            out _);
    }

    /// <summary>
    /// Tests a bounded set of points across the candidate's perceptible world-space footprint. Host visibility-layer
    /// compatibility is checked before geometry so entities hidden by Station visibility mechanics (for example covered
    /// subfloor cables/pipes) never become COGR visual evidence merely because their server entities are nearby and
    /// geometrically unobstructed. The candidate itself is ignored as an obstruction only for rays terminating on that
    /// candidate; this never grants visibility to a different entity behind the candidate. A partially exposed footprint
    /// therefore remains perceptible while a fully occluded entity remains absent.
    /// </summary>
    private bool TryGetVisualFootprintQuality(
        EntityUid observer,
        NativeCandidate candidate,
        double observedRange,
        out double visibilityQuality) =>
        TryGetVisualFootprintQuality(
            observer,
            candidate.Entity,
            observedRange,
            out visibilityQuality);

    private bool TryGetVisualFootprintQuality(
        EntityUid observer,
        EntityUid target,
        double observedRange,
        out double visibilityQuality)
    {
        visibilityQuality = 0;
        if (Deleted(observer) || Deleted(target))
            return false;

        if (!IsVisibilityLayerAvailableToObserver(observer, target))
            return false;

        var transform = Transform(target);
        if (transform.MapID == MapId.Nullspace)
            return false;

        var observerTransform = Transform(observer);
        if (observerTransform.MapID != transform.MapID)
            return false;

        var bounds = _lookup.GetWorldAABB(target, transform);
        var center = bounds.Center;
        var halfWidth = MathF.Max(0f, (bounds.Right - bounds.Left) * 0.5f * COGRSpatialPolicy.VisualFootprintSampleExtentFraction);
        var halfHeight = MathF.Max(0f, (bounds.Top - bounds.Bottom) * 0.5f * COGRSpatialPolicy.VisualFootprintSampleExtentFraction);

        Span<Vector2> samples = stackalloc Vector2[COGRSpatialPolicy.VisualFootprintSampleCount]
        {
            center,
            center + new Vector2(-halfWidth, 0f),
            center + new Vector2(halfWidth, 0f),
            center + new Vector2(0f, -halfHeight),
            center + new Vector2(0f, halfHeight),
            center + new Vector2(-halfWidth, -halfHeight),
            center + new Vector2(-halfWidth, halfHeight),
            center + new Vector2(halfWidth, -halfHeight),
            center + new Vector2(halfWidth, halfHeight),
        };

        var visibleSamples = 0;
        foreach (var sample in samples)
        {
            if (_interaction.InRangeUnobstructed(
                    observer,
                    new MapCoordinates(sample, transform.MapID),
                    (float)observedRange,
                    predicate: entity => entity == target))
            {
                visibleSamples++;
            }
        }

        visibilityQuality = visibleSamples / (double)samples.Length;
        return visibilityQuality >= COGRSpatialPolicy.MinimumVisualFootprintFraction;
    }

    /// <summary>
    /// Applies Station's own viewer/target visibility-layer contract before COGR performs geometric visibility work.
    /// Missing components retain ordinary Normal-layer compatibility for adapter bodies/entities that do not participate
    /// in specialized visibility mechanics. Specialized host faculties remain authoritative: if Station expands the
    /// observer eye mask (for example a subfloor scanner), COGR can perceive the newly available layer without receiving
    /// prototype identities or hidden coordinates.
    /// </summary>
    private bool IsVisibilityLayerAvailableToObserver(EntityUid observer, EntityUid target)
    {
        var targetLayer = (int) VisibilityFlags.Normal;
        if (TryComp(target, out VisibilityComponent? visibility))
            targetLayer = visibility.Layer;

        var observerMask = (int) VisibilityFlags.Normal;
        if (TryComp(observer, out EyeComponent? eye))
            observerMask = eye.VisibilityMask;

        return (observerMask & targetLayer) != 0;
    }
}
