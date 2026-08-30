using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using Content.Shared.Hands.Components;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRBoundedPerceptionSystem
{
    private readonly Dictionary<ReferenceCacheKey, string> _passiveVisualSemanticFingerprints = new();

    /// <summary>
    /// Resolves one privileged host-change entity to the opaque semantic surface that passive visual attention may
    /// legitimately expose. Direct semantic entities remain themselves. A directly displayed held item resolves to its
    /// visible humanoid holder so focused inspection can reconstruct the hand/hold relationship through the ordinary
    /// actor projection. Hidden containment and engine-only entities produce no passive visual source.
    /// </summary>
    public bool TryResolvePassiveVisualSemanticSource(
        EntityUid observer,
        EntityUid changedSource,
        double observedRange,
        out EntityUid semanticSource)
    {
        semanticSource = default;
        if (!double.IsFinite(observedRange) || observedRange <= 0)
            throw new ArgumentOutOfRangeException(nameof(observedRange));
        if (Deleted(observer) || Deleted(changedSource) || changedSource == observer)
            return false;

        if (!_containers.IsEntityOrParentInContainer(changedSource))
        {
            return TryAcceptDirectPassiveVisualSemanticSource(
                observer,
                changedSource,
                observedRange,
                out semanticSource);
        }

        // Direct hand contents are the one contained visual surface already reintroduced by the bounded projector.
        // Resolve their passive wake to the holder rather than leaking the contained host entity as a direct focus target.
        if (!_containers.TryGetContainingContainer((changedSource, null, null), out var containing))
            return false;

        var holder = containing.Owner;
        if (holder == observer || Deleted(holder))
            return false;
        if (!TryComp<HandsComponent>(holder, out var hands) || !hands.ShowInHands)
            return false;
        if (!TryCreateCandidate(holder, 0d, hints: null, out var holderCandidate)
            || holderCandidate.Category != "actor"
            || !holderCandidate.IsHumanoid)
        {
            return false;
        }

        var directlyHeld = false;
        foreach (var handId in _hands.EnumerateHands((holder, hands)))
        {
            if (!_hands.TryGetHeldItem(
                    (holder, hands),
                    handId,
                    out var held,
                    hideVirtualItems: true)
                || held.Value != changedSource)
            {
                continue;
            }

            directlyHeld = TryCreateCandidate(changedSource, 0d, hints: null, out _);
            break;
        }

        if (!directlyHeld)
            return false;

        return TryAcceptDirectPassiveVisualSemanticSource(
            observer,
            holder,
            observedRange,
            out semanticSource);
    }

    /// <summary>
    /// Advances the last observer-visible semantic fingerprint for one already-resolved passive visual source.
    /// The return value reports whether the source remains projectable; <paramref name="changed"/> reports whether the
    /// current surface differs from the last emitted passive baseline. This is adapter-side transduction state only; it is
    /// neither cognitive memory nor a cooldown/refractory rule.
    /// </summary>
    public bool TryAdvancePassiveVisualSemanticFingerprint(
        EntityUid observer,
        EntityUid semanticSource,
        double observedRange,
        ConnectionId connectionId,
        BodyId bodyId,
        uint bodyGeneration,
        out bool changed)
    {
        changed = false;
        if (!double.IsFinite(observedRange) || observedRange <= 0)
            throw new ArgumentOutOfRangeException(nameof(observedRange));
        if (bodyGeneration == 0)
            throw new ArgumentOutOfRangeException(nameof(bodyGeneration));
        if (Deleted(observer) || Deleted(semanticSource) || semanticSource == observer)
            return false;
        if (_containers.IsEntityOrParentInContainer(semanticSource))
            return false;

        if (!Transform(observer).Coordinates.TryDistance(
                EntityManager,
                Transform(semanticSource).Coordinates,
                out var distance)
            || distance > observedRange
            || !TryCreateCandidate(semanticSource, distance, hints: null, out var candidate))
        {
            return false;
        }

        var builder = new StringBuilder();
        AppendFingerprintToken(builder, candidate.Category);
        AppendFeatureFingerprint(builder, CreateFeatures(observer, candidate));

        // Focused actor projection exposes stable hand subreferents and, when Station renders hand contents externally,
        // hold relations plus the held entities themselves. Include exactly that externally visible relational surface so
        // drawing/swapping/changing a visible item recruits attention while hidden inventory/internal component churn does not.
        if (candidate.Category == "actor"
            && candidate.IsHumanoid
            && TryComp<HandsComponent>(candidate.Entity, out var hands))
        {
            var examinedHands = 0;
            foreach (var handId in _hands
                         .EnumerateHands((candidate.Entity, hands))
                         .OrderBy(static handId => handId, StringComparer.Ordinal))
            {
                if (examinedHands >= SemanticRelationalEvidenceLimits.MaximumSubreferentsPerObservation)
                    break;

                examinedHands++;
                AppendFingerprintToken(builder, "hand");
                AppendFingerprintToken(builder, handId);

                if (!hands.ShowInHands
                    || !TryGetVisibleHeldEntity(candidate.Entity, hands, handId, out var heldEntity)
                    || !TryCreateCandidate(heldEntity, candidate.Distance, hints: null, out var heldCandidate))
                {
                    continue;
                }

                AppendFingerprintToken(builder, "hold");
                AppendFingerprintToken(builder, handId);
                AppendFingerprintToken(builder, heldEntity.ToString());
                AppendFingerprintToken(builder, heldCandidate.Category);
                AppendFeatureFingerprint(builder, CreateFeatures(observer, heldCandidate));
            }
        }

        var key = new ReferenceCacheKey(
            semanticSource,
            connectionId,
            bodyId,
            bodyGeneration);
        var fingerprint = builder.ToString();
        if (_passiveVisualSemanticFingerprints.TryGetValue(key, out var previous)
            && string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        _passiveVisualSemanticFingerprints[key] = fingerprint;
        changed = true;
        return true;
    }

    private static void AppendFeatureFingerprint(
        StringBuilder builder,
        IReadOnlyList<ObservedFeature> features)
    {
        foreach (var feature in features
                     .OrderBy(static feature => feature.Category, StringComparer.Ordinal)
                     .ThenBy(static feature => feature.FeatureType, StringComparer.Ordinal)
                     .ThenBy(static feature => FormatFingerprintValue(feature.Value), StringComparer.Ordinal))
        {
            AppendFingerprintToken(builder, feature.Category);
            AppendFingerprintToken(builder, feature.FeatureType);
            AppendFingerprintToken(builder, FormatFingerprintValue(feature.Value));
            AppendFingerprintToken(
                builder,
                feature.Confidence?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty);
        }
    }

    private static string FormatFingerprintValue(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static void AppendFingerprintToken(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }

    private bool TryAcceptDirectPassiveVisualSemanticSource(
        EntityUid observer,
        EntityUid candidateSource,
        double observedRange,
        out EntityUid semanticSource)
    {
        semanticSource = default;
        if (Deleted(candidateSource)
            || candidateSource == observer
            || _containers.IsEntityOrParentInContainer(candidateSource)
            || !TryCreateCandidate(candidateSource, 0d, hints: null, out _)
            || !IsEntityCurrentlyVisuallyAvailable(observer, candidateSource, observedRange))
        {
            return false;
        }

        semanticSource = candidateSource;
        return true;
    }
}
