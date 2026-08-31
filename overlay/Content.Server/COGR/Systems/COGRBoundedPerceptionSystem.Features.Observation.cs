using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using COGR.Contracts.Messages;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using Content.Server.Construction.Components;
using Content.Server.DeviceLinking.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Storage.Components;
using Content.Shared.Tag;
using Content.Shared.Tools.Components;
using Robust.Shared.Containers;
using Robust.Shared.Log;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRBoundedPerceptionSystem
{
    private Observation CreateObservation(
        PerceptionRequest request,
        EntityUid observer,
        SimTick currentTick,
        NativeCandidate candidate,
        COGRReferenceRegistry registry,
        double observedRange,
        double visibilityQuality)
    {
        var environmentReference = GetOrCreateReference(
            request,
            currentTick,
            candidate,
            registry);
        var features = CreateFeatures(observer, candidate);
        var distanceRatio = observedRange <= 0
            ? 1.0
            : Math.Clamp(candidate.Distance / observedRange, 0, 1);
        var baseSalience = candidate.Category switch
        {
            "actor" => 0.9,
            "door" => 0.8,
            "control" => 0.75,
            "container" or "machine" or "barrier" => 0.65,
            "handheld_tool" or "handheld_item" => 0.6,
            "structure" => 0.55,
            _ => 0.45,
        };
        var salience = Math.Clamp(
            baseSalience + (candidate.HintScore * 0.08) - (distanceRatio * 0.3),
            0.1,
            1.0);
        var confidence = Math.Clamp(0.35 + (Math.Clamp(visibilityQuality, 0, 1) * 0.6), 0.35, 0.95);
        var relationalProjection = CreateRelationalProjection(
            request,
            currentTick,
            candidate,
            environmentReference,
            registry,
            confidence);
        var observationCategory = candidate.Category == "actor"
            ? candidate.IsHumanoid
                ? "person"
                : "entity"
            : candidate.Category;

        LogPrivilegedItemClassification(
            request,
            currentTick,
            candidate,
            environmentReference);

        return new Observation
        {
            ObservationId = Guid.CreateVersion7(),
            EnvironmentRef = environmentReference,
            Features = features,
            Subreferents = relationalProjection.Subreferents,
            Relations = relationalProjection.Relations,
            Location = null,
            Salience = salience,
            Confidence = confidence,
            TemporalQuality = "current",
            AcquisitionMode = "focused_query",
            Category = observationCategory,
        };
    }

    /// <summary>
    /// Emits privileged adapter-only evidence for item/tool observations so deep live diagnostics can correlate COGR's coarse
    /// category with the actual SS14 entity that produced it. Native entity/prototype identity remains server-local and is
    /// never added to the observation transported to cognition. This stays below ordinary INFO output because focused
    /// perception can classify many items on every observation refresh.
    /// </summary>
    private void LogPrivilegedItemClassification(
        PerceptionRequest request,
        SimTick currentTick,
        NativeCandidate candidate,
        EnvironmentRef environmentReference)
    {
        if (candidate.Category is not ("handheld_tool" or "handheld_item"))
            return;

        var prototypeId = MetaData(candidate.Entity).EntityPrototype?.ID.ToString() ?? "<none>";
        var anchored = Transform(candidate.Entity).Anchored;
        var contained = _containers.IsEntityOrParentInContainer(candidate.Entity);
        var hasItemComponent = HasComp<ItemComponent>(candidate.Entity);
        var hasToolComponent = HasComp<ToolComponent>(candidate.Entity);

        _sawmill.Debug(
            "Privileged item perception: tick={0} agent={1} entity={2} prototype={3} category={4} envRef={5} anchored={6} contained={7} itemComponent={8} toolComponent={9} distance={10}",
            currentTick.Value,
            request.AgentId,
            candidate.Entity,
            prototypeId,
            candidate.Category,
            environmentReference,
            anchored,
            contained,
            hasItemComponent,
            hasToolComponent,
            candidate.Distance);
    }

    private IReadOnlyList<ObservedFeature> CreateFeatures(
        EntityUid observer,
        NativeCandidate candidate)
    {
        var features = candidate.Category switch
        {
            "door" => CreateDoorFeatures(candidate.Door!),
            "actor" => CreateActorFeatures(candidate.Entity, candidate.IsHumanoid),
            "control" => CreateControlFeatures(candidate.Control!),
            "container" => CreateContainerFeatures(candidate.Storage!),
            "machine" => CreateMachineFeatures(candidate.Entity),
            "barrier" => CreateBarrierFeatures(candidate.IsWindow),
            "handheld_tool" or "handheld_item" => CreateItemFeatures(candidate),
            "structure" => CreateStructureFeatures(candidate.Entity),
            _ => CreateGenericObjectFeatures(candidate.Entity),
        };

        AddSpatialFeatures(features, observer, candidate.Entity, candidate.Distance);
        AddMotionFeature(features, candidate.Entity, candidate.Category);
        return features;
    }

    /// <summary>
    /// Returns the same body-scoped opaque identity used by focused projection for an
    /// independently actor-valid passive cue. A heard-only reference is addressable but
    /// does not become action-authorizing until current replica evidence contains it.
    /// </summary>
    internal EnvironmentRef GetOrCreateReferenceForObservedEntity(
        EntityUid entity,
        ConnectionId connectionId,
        AgentId agentId,
        BodyId bodyId,
        uint bodyGeneration,
        SimTick currentTick,
        string category,
        COGRReferenceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (bodyGeneration == 0)
            throw new ArgumentOutOfRangeException(nameof(bodyGeneration));
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("A semantic reference category is required.", nameof(category));

        var key = new ReferenceCacheKey(entity, connectionId, bodyId, bodyGeneration);
        if (_referenceCache.TryGetValue(key, out var existing))
            return existing;

        var issued = registry.IssueReference(
            entity,
            new ReferenceScope
            {
                ConnectionId = connectionId,
                BodyId = bodyId,
                BodyGeneration = bodyGeneration,
                IssuedAtTick = currentTick,
            },
            category,
            agentId);
        _referenceCache[key] = issued;
        return issued;
    }

    private EnvironmentRef GetOrCreateReference(
        PerceptionRequest request,
        SimTick currentTick,
        NativeCandidate candidate,
        COGRReferenceRegistry registry) =>
        GetOrCreateReferenceForObservedEntity(
            candidate.Entity,
            request.ConnectionId,
            request.AgentId,
            request.BodyId,
            request.BodyGeneration,
            currentTick,
            candidate.Category,
            registry);
}
