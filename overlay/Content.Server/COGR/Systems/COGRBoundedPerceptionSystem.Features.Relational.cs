using System;
using System.Collections.Generic;
using System.Linq;
using COGR.Contracts.Messages;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Time;
using Content.Shared.Hands.Components;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRBoundedPerceptionSystem
{
    private const string HandSubreferentDomain = "hand";
    private const string HandCategoryHint = "hand";
    private const string VisibleContentsSubreferentDomain = "visible-contents";
    private const string VisibleContentsSubreferentPart = "primary";
    private const string HoldRelationHint = "hold";
    private const string PartRelationHint = "part";
    private const string ContainRelationHint = "contain";

    private readonly Dictionary<SubreferentCacheKey, SubreferentRef> _subreferentCache = new();

    /// <summary>
    /// Adds only directly hand-held entities that Station itself renders externally on a humanoid actor.
    /// Other container contents remain absent from the direct visual candidate set.
    /// </summary>
    private void AppendVisibleHeldCandidates(
        NativeCandidate actorCandidate,
        IReadOnlyList<string>? hints,
        List<NativeCandidate> candidates,
        ref CategoryCounts discoveredByCategory)
    {
        if (!actorCandidate.IsHumanoid ||
            !TryComp<HandsComponent>(actorCandidate.Entity, out var hands) ||
            !hands.ShowInHands)
        {
            return;
        }

        var examinedHands = 0;
        foreach (var handId in _hands
                     .EnumerateHands((actorCandidate.Entity, hands))
                     .OrderBy(static handId => handId, StringComparer.Ordinal))
        {
            if (examinedHands >= SemanticRelationalEvidenceLimits.MaximumSubreferentsPerObservation)
                break;

            examinedHands++;
            if (!TryGetVisibleHeldEntity(actorCandidate.Entity, hands, handId, out var heldEntity) ||
                !TryCreateCandidate(heldEntity, actorCandidate.Distance, hints, out var heldCandidate))
            {
                continue;
            }

            discoveredByCategory = discoveredByCategory.Increment(heldCandidate.Category);
            candidates.Add(heldCandidate);
        }
    }

    private ObservationRelationalProjection CreateRelationalProjection(
        PerceptionRequest request,
        SimTick currentTick,
        NativeCandidate candidate,
        EnvironmentRef environmentReference,
        COGRReferenceRegistry registry,
        double confidence)
    {
        return candidate.Category switch
        {
            "actor" => CreateActorRelationalProjection(
                request,
                currentTick,
                candidate,
                environmentReference,
                registry,
                confidence),
            "handheld_tool" or "handheld_item" => CreateVisibleContentsRelationalProjection(
                request,
                candidate,
                environmentReference,
                confidence),
            _ => ObservationRelationalProjection.Empty,
        };
    }

    private ObservationRelationalProjection CreateActorRelationalProjection(
        PerceptionRequest request,
        SimTick currentTick,
        NativeCandidate actorCandidate,
        EnvironmentRef actorReference,
        COGRReferenceRegistry registry,
        double confidence)
    {
        if (!actorCandidate.IsHumanoid ||
            !TryComp<HandsComponent>(actorCandidate.Entity, out var hands))
        {
            return ObservationRelationalProjection.Empty;
        }

        var subreferents = new List<ObservedSubreferent>();
        var relations = new List<ObservedRelation>();
        var actorReferent = SituatedReferent.FromEnvironment(actorReference);

        foreach (var handId in _hands
                     .EnumerateHands((actorCandidate.Entity, hands))
                     .OrderBy(static handId => handId, StringComparer.Ordinal))
        {
            if (subreferents.Count >= SemanticRelationalEvidenceLimits.MaximumSubreferentsPerObservation)
                break;

            var handReference = GetOrCreateSubreferentReference(
                actorCandidate.Entity,
                HandSubreferentDomain,
                handId,
                request.ConnectionId,
                request.BodyId,
                request.BodyGeneration);
            var handReferent = SituatedReferent.FromSubreferent(handReference);

            subreferents.Add(new ObservedSubreferent
            {
                Reference = handReference,
                Features = Array.Empty<ObservedFeature>(),
                Confidence = confidence,
                Category = HandCategoryHint,
            });

            if (relations.Count < SemanticRelationalEvidenceLimits.MaximumRelationsPerObservation)
            {
                relations.Add(new ObservedRelation
                {
                    Subject = handReferent,
                    RelationType = PartRelationHint,
                    Target = actorReferent,
                    Confidence = confidence,
                });
            }

            if (!hands.ShowInHands ||
                relations.Count >= SemanticRelationalEvidenceLimits.MaximumRelationsPerObservation ||
                !TryGetVisibleHeldEntity(actorCandidate.Entity, hands, handId, out var heldEntity) ||
                !TryCreateCandidate(heldEntity, actorCandidate.Distance, hints: null, out var heldCandidate))
            {
                continue;
            }

            var heldReference = GetOrCreateReferenceForObservedEntity(
                heldEntity,
                request.ConnectionId,
                request.AgentId,
                request.BodyId,
                request.BodyGeneration,
                currentTick,
                heldCandidate.Category,
                registry);
            relations.Add(new ObservedRelation
            {
                Subject = handReferent,
                RelationType = HoldRelationHint,
                Target = SituatedReferent.FromEnvironment(heldReference),
                Confidence = confidence,
            });
        }

        return new ObservationRelationalProjection(subreferents, relations);
    }

    private ObservationRelationalProjection CreateVisibleContentsRelationalProjection(
        PerceptionRequest request,
        NativeCandidate itemCandidate,
        EnvironmentRef itemReference,
        double confidence)
    {
        if (!HasVisibleLiquidContents(itemCandidate.Entity))
            return ObservationRelationalProjection.Empty;

        var contentsReference = GetOrCreateSubreferentReference(
            itemCandidate.Entity,
            VisibleContentsSubreferentDomain,
            VisibleContentsSubreferentPart,
            request.ConnectionId,
            request.BodyId,
            request.BodyGeneration);
        var contentsReferent = SituatedReferent.FromSubreferent(contentsReference);
        var contentsConfidence = Math.Min(confidence, 0.9);

        return new ObservationRelationalProjection(
            [
                new ObservedSubreferent
                {
                    Reference = contentsReference,
                    Features = [ObservedFeature.State("liquid", contentsConfidence)],
                    Confidence = contentsConfidence,
                },
            ],
            [
                new ObservedRelation
                {
                    Subject = SituatedReferent.FromEnvironment(itemReference),
                    RelationType = ContainRelationHint,
                    Target = contentsReferent,
                    Confidence = contentsConfidence,
                },
            ]);
    }

    private bool TryGetVisibleHeldEntity(
        EntityUid actor,
        HandsComponent hands,
        string handId,
        out EntityUid heldEntity)
    {
        heldEntity = default;
        if (!_hands.TryGetHeldItem(
                (actor, hands),
                handId,
                out var held,
                hideVirtualItems: true))
        {
            return false;
        }

        var candidate = held.Value;
        if (Deleted(candidate))
            return false;

        heldEntity = candidate;
        return true;
    }

    private SubreferentRef GetOrCreateSubreferentReference(
        EntityUid parentEntity,
        string sourceDomain,
        string sourcePartId,
        ConnectionId connectionId,
        BodyId bodyId,
        uint bodyGeneration)
    {
        if (string.IsNullOrWhiteSpace(sourceDomain))
            throw new ArgumentException("A subreferent source domain is required.", nameof(sourceDomain));
        if (string.IsNullOrWhiteSpace(sourcePartId))
            throw new ArgumentException("A subreferent source part identity is required.", nameof(sourcePartId));
        if (bodyGeneration == 0)
            throw new ArgumentOutOfRangeException(nameof(bodyGeneration));

        var key = new SubreferentCacheKey(
            parentEntity,
            sourceDomain,
            sourcePartId,
            connectionId,
            bodyId,
            bodyGeneration);
        if (_subreferentCache.TryGetValue(key, out var existing))
            return existing;

        var created = SubreferentRef.NewRef();
        _subreferentCache.Add(key, created);
        return created;
    }

    private void RemoveCachedSubreferents(Func<SubreferentCacheKey, bool> predicate)
    {
        var staleKeys = _subreferentCache.Keys.Where(predicate).ToList();
        foreach (var key in staleKeys)
            _subreferentCache.Remove(key);
    }

    private readonly record struct SubreferentCacheKey(
        EntityUid ParentEntity,
        string SourceDomain,
        string SourcePartId,
        ConnectionId ConnectionId,
        BodyId BodyId,
        uint BodyGeneration);

    private readonly record struct ObservationRelationalProjection(
        IReadOnlyList<ObservedSubreferent> Subreferents,
        IReadOnlyList<ObservedRelation> Relations)
    {
        public static readonly ObservationRelationalProjection Empty = new(
            Array.Empty<ObservedSubreferent>(),
            Array.Empty<ObservedRelation>());
    }
}
