using System;
using COGR.Contracts.Messages;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using Content.Shared.COGR.Components;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRPassivePerceptionSystem
{
    /// <summary>
    /// Publishes one cheap visual cue for a host change that resolves to an externally inspectable semantic visual surface.
    /// Generic host dirtiness is admitted only when that observer-relative semantic surface changed since its last emitted
    /// passive cue. The cue exposes no object semantics beyond a passive-only opaque source reference; cognition must recruit
    /// bounded focused perception when Signal/Concern/procedure relevance decides the changed source deserves more detail.
    /// </summary>
    public bool TryPublishVisualSourceCue(
        SemanticReplicaOwner owner,
        EntityUid source,
        string eventClass,
        double salience)
    {
        if (string.IsNullOrWhiteSpace(eventClass))
            throw new ArgumentException("A passive visual event class is required.", nameof(eventClass));
        if (!double.IsFinite(salience) || salience < 0 || salience > 1)
            throw new ArgumentOutOfRangeException(nameof(salience));
        if (Deleted(source))
            return false;

        var lease = _authority.ResolveBoundLease(owner.AgentId, owner.ConnectionId);
        if (!lease.HasValue)
            return false;

        var body = _authority.ResolveBoundBody(
            owner.AgentId,
            lease.Value.BodyId,
            owner.ConnectionId,
            lease.Value.Generation);
        if (!body.HasValue || body.Value == source)
            return false;

        if (!TryComp<COGRControlledComponent>(body.Value, out var controlled)
            || !TryGetContext((body.Value, controlled), out var context)
            || context.ConnectionId != owner.ConnectionId
            || context.AgentId != owner.AgentId
            || context.BodyId != lease.Value.BodyId
            || context.BodyGeneration != lease.Value.Generation)
        {
            return false;
        }

        if (!_boundedPerception.TryResolvePassiveVisualSemanticSource(
                body.Value,
                source,
                COGRSpatialPolicy.DefaultVisualHorizon,
                out var semanticSource))
        {
            return false;
        }

        var registry = _adapter.ReferenceRegistry;
        if (registry is null)
            return false;

        // EntityDirtied is only a privileged host wake hint. Do not tell cognition that a visible source changed unless
        // the same bounded semantic surface focused perception could expose has actually changed. Because the cache key is
        // the resolved semantic source, multiple raw held/internal host changes that map to one visible actor collapse here.
        // Movement and appearance remain real embodied events even when the actor returns to a previously seen state; they
        // still refresh the semantic baseline so later unrelated host dirtiness stays quiet.
        if (!_boundedPerception.TryAdvancePassiveVisualSemanticFingerprint(
                body.Value,
                semanticSource,
                COGRSpatialPolicy.DefaultVisualHorizon,
                context.ConnectionId,
                context.BodyId,
                context.BodyGeneration,
                out var semanticChanged))
        {
            return false;
        }

        if (string.Equals(eventClass, "visual_source_changed", StringComparison.Ordinal)
            && !semanticChanged)
        {
            return false;
        }

        var sourceReference = _boundedPerception.GetOrCreateReferenceForObservedEntity(
            semanticSource,
            context.ConnectionId,
            context.AgentId,
            context.BodyId,
            context.BodyGeneration,
            context.Tick,
            "passive_visual_source",
            registry);

        Publish(
            context,
            PerceptionCategory.Environmental,
            new PerceptualEvent
            {
                EvidenceId = Guid.CreateVersion7(),
                ObservedAtTick = context.Tick,
                AgentId = context.AgentId,
                BodyId = context.BodyId,
                CausalTraceId = CausalTraceId.FromGuid(Guid.CreateVersion7()),
                SourceQueryId = null,
                ObservationType = ObservationType.PassiveSensory,
                UrgencyHint = 25,
                EventClass = eventClass,
                Features =
                [
                    new ObservedFeature
                    {
                        Category = "state",
                        FeatureType = "visual_change_available",
                        Value = true,
                        Confidence = 0.8,
                    },
                    ObservedFeature.Relation(
                        "source_reference_scope",
                        "passive_only",
                        1.0),
                ],
                Transmission = new CueTransmission
                {
                    EmbodimentProfile = GenericHumanoidEmbodimentProfile,
                    Modality = CueModality.Visual,
                    Channel = "ambient_visual_change",
                    RangeFraction = null,
                    LineOfSight = true,
                    Occlusion = 0.0,
                    OcclusionModel = LosBinaryOcclusionModel,
                    Attenuation = null,
                    PayloadIntegrity = 1.0,
                    PayloadMutated = false,
                    MutationKinds = Array.Empty<string>(),
                    SourceResolution = CueSourceResolution.Direct,
                    DirectionResolution = CueDirectionResolution.Exact,
                },
                SourceRef = sourceReference,
                SpatialRelation = "within_visual_horizon",
                Salience = salience,
            });

        return true;
    }
}
