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
    private ulong? _lastProjectionTick;

    /// <summary>
    /// True when the authoritative visual projector has already performed spatial work during
    /// the current simulation tick. Other COGR visual-maintenance systems use this to avoid
    /// stacking another bounded projection onto the same SS14 update.
    /// </summary>
    public bool ProjectionPerformedThisTick =>
        _lastProjectionTick == (ulong)_timing.CurTick.Value;

    /// <summary>
    /// Reuses the authoritative bounded visual projector for passive semantic-replica
    /// snapshots and focused entity inspection. Callers must resolve and validate exact body authority before invoking it.
    /// A focused entity constrains discovery but does not change the observer-relative spatial frame or bypass visibility.
    /// </summary>
    public PerceptionResult ProjectReplica(
        PerceptionRequest request,
        EntityUid observer,
        SimTick currentTick,
        EntityUid? focusedEntity = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var registry = _adapter.ReferenceRegistry;
        if (registry == null)
        {
            return CreateFailureResult(
                request,
                currentTick,
                OmissionCategory.AdapterCoverageLimited,
                "Station opaque environment-reference storage is unavailable.");
        }

        var requestedCandidates = request.Budget.MaxEntitiesConsidered ?? DefaultCandidateBudget;
        var requestedObservations = request.Budget.MaxObservationsReturned ?? DefaultObservationBudget;
        var requestedDistance = request.Budget.MaxDistance ??
            (focusedEntity.HasValue ? COGRSpatialPolicy.DefaultVisualHorizon : DefaultVisualRange);
        var requestedProcessingMs = request.Budget.MaxProcessingTimeMs ?? DefaultProcessingBudgetMs;

        var maxCandidates = Math.Clamp(requestedCandidates, 0, MaximumCandidateBudget);
        var maxObservations = Math.Clamp(requestedObservations, 0, MaximumObservationBudget);
        var maxDistance = Math.Clamp(requestedDistance, 0, MaximumVisualRange);
        var maxProcessingMs = Math.Clamp(requestedProcessingMs, 1, MaximumProcessingBudgetMs);

        var exhaustionReason = BudgetExhaustionReason.None;
        if (requestedCandidates > MaximumCandidateBudget ||
            requestedObservations > MaximumObservationBudget ||
            requestedDistance > MaximumVisualRange ||
            requestedProcessingMs > MaximumProcessingBudgetMs)
        {
            exhaustionReason = BudgetExhaustionReason.ServerLimitClamped;
        }

        _lastProjectionTick = currentTick.Value;

        var nearby = new HashSet<EntityUid>();
        if (focusedEntity.HasValue)
        {
            nearby.Add(focusedEntity.Value);
        }
        else
        {
            _lookup.GetEntitiesInRange(
                Transform(observer).Coordinates,
                (float)maxDistance,
                nearby);
        }

        var candidates = new List<NativeCandidate>();
        var discoveredByCategory = new CategoryCounts();

        foreach (var entity in nearby)
        {
            if (stopwatch.ElapsedMilliseconds >= maxProcessingMs)
            {
                if (exhaustionReason == BudgetExhaustionReason.None)
                    exhaustionReason = BudgetExhaustionReason.TimeBudgetExhausted;
                break;
            }

            if (entity == observer || Deleted(entity) || HasComp<MapGridComponent>(entity))
                continue;

            // Hidden containment remains outside the direct visual projection. Direct hand contents
            // are reintroduced only through the externally visible actor-hand projection below.
            if (_containers.IsEntityOrParentInContainer(entity))
                continue;

            if (!Transform(observer).Coordinates.TryDistance(
                    EntityManager,
                    Transform(entity).Coordinates,
                    out var distance) ||
                distance > maxDistance)
            {
                continue;
            }

            if (!TryCreateCandidate(entity, distance, request.SearchConceptHints, out var candidate))
                continue;

            discoveredByCategory = discoveredByCategory.Increment(candidate.Category);
            candidates.Add(candidate);
            if (candidate.Category == "actor")
            {
                AppendVisibleHeldCandidates(
                    candidate,
                    request.SearchConceptHints,
                    candidates,
                    ref discoveredByCategory);
            }
        }

        candidates.Sort(static (left, right) =>
        {
            var hint = right.HintScore.CompareTo(left.HintScore);
            if (hint != 0)
                return hint;

            var semanticPriority = right.SemanticPriority.CompareTo(left.SemanticPriority);
            if (semanticPriority != 0)
                return semanticPriority;

            var distance = left.Distance.CompareTo(right.Distance);
            if (distance != 0)
                return distance;

            return StringComparer.Ordinal.Compare(
                left.Entity.ToString(),
                right.Entity.ToString());
        });

        var observations = new List<Observation>(Math.Min(maxObservations, candidates.Count));
        var evaluated = 0;
        var evaluatedByCategory = new CategoryCounts();
        var emittedByCategory = new CategoryCounts();

        foreach (var candidate in candidates)
        {
            if (stopwatch.ElapsedMilliseconds >= maxProcessingMs)
            {
                if (exhaustionReason == BudgetExhaustionReason.None)
                    exhaustionReason = BudgetExhaustionReason.TimeBudgetExhausted;
                break;
            }

            if (observations.Count >= maxObservations)
            {
                if (exhaustionReason == BudgetExhaustionReason.None)
                    exhaustionReason = BudgetExhaustionReason.ObservationLimitReached;
                break;
            }

            if (evaluated >= maxCandidates)
            {
                if (exhaustionReason == BudgetExhaustionReason.None)
                    exhaustionReason = BudgetExhaustionReason.CandidateLimitReached;
                break;
            }

            evaluated++;
            evaluatedByCategory = evaluatedByCategory.Increment(candidate.Category);

            if (!TryGetVisualFootprintQuality(
                    observer,
                    candidate,
                    maxDistance,
                    out var visibilityQuality))
            {
                continue;
            }

            observations.Add(CreateObservation(
                request,
                observer,
                currentTick,
                candidate,
                registry,
                maxDistance,
                visibilityQuality));
            emittedByCategory = emittedByCategory.Increment(candidate.Category);
        }

        if (exhaustionReason == BudgetExhaustionReason.None && candidates.Count > evaluated)
            exhaustionReason = BudgetExhaustionReason.CandidateLimitReached;

        stopwatch.Stop();

        var diagnostics = new PerceptionDiagnostics
        {
            CandidatesDiscovered = candidates.Count,
            CandidatesEvaluated = evaluated,
            ObservationsEmitted = observations.Count,
            ElapsedProjectionMs = stopwatch.Elapsed.TotalMilliseconds,
            ExhaustionReason = exhaustionReason,
            DiscoveredByCategory = discoveredByCategory,
            EvaluatedByCategory = evaluatedByCategory,
            EmittedByCategory = emittedByCategory,
        };

        _sawmill.Debug("Perception diagnostics: {0}", diagnostics.ToSummary());

        return new PerceptionResult
        {
            EvidenceId = Guid.CreateVersion7(),
            ObservedAtTick = currentTick,
            AgentId = request.AgentId,
            BodyId = request.BodyId,
            CausalTraceId = request.CausalTraceId,
            SourceQueryId = request.RequestId,
            ObservationType = ObservationType.Sensory,
            UrgencyHint = 0,
            RequestId = request.RequestId,
            CompletionState = exhaustionReason != BudgetExhaustionReason.None
                ? PerceptionCompletionState.BudgetExhausted
                : PerceptionCompletionState.Complete,
            ObservedRegionRadius = maxDistance,
            Observations = observations,
            Omissions = CreateOmissions(exhaustionReason),
        };
    }

    private bool TryCreateCandidate(
        EntityUid entity,
        double distance,
        IReadOnlyList<string>? hints,
        out NativeCandidate candidate)
    {
        TryComp(entity, out DoorComponent? door);
        TryComp(entity, out SignalSwitchComponent? control);
        TryComp(entity, out EntityStorageComponent? storage);

        var isItem = HasComp<ItemComponent>(entity);
        var isTool = isItem && HasComp<ToolComponent>(entity);
        var isActor = HasComp<MobStateComponent>(entity);
        var isHumanoid = HasComp<HumanoidProfileComponent>(entity);
        var isMachine = HasComp<MachineComponent>(entity);
        var isWall = _tags.HasTag(entity, WallTag);
        var prototypeId = MetaData(entity).EntityPrototype?.ID.ToString();
        var isWindow = prototypeId?.Contains("Window", StringComparison.OrdinalIgnoreCase) == true;
        var isPhysical = HasComp<PhysicsComponent>(entity);
        var hasPrototype = MetaData(entity).EntityPrototype != null;

        var category = door != null
            ? "door"
            : isActor
                ? "actor"
                : control != null
                    ? "control"
                    : storage != null
                        ? "container"
                        : isMachine
                            ? "machine"
                            : isTool
                                ? "handheld_tool"
                                : isItem
                                    ? "handheld_item"
                                    : isWall || isWindow
                                        ? "barrier"
                                        : isPhysical && hasPrototype && Transform(entity).Anchored
                                            ? "structure"
                                            : isPhysical && hasPrototype
                                                ? "generic_object"
                                                : null;

        if (category == null)
        {
            candidate = default!;
            return false;
        }

        candidate = new NativeCandidate(
            entity,
            distance,
            door,
            control,
            storage,
            isTool,
            isHumanoid,
            isWindow,
            category,
            HintScore(category, hints),
            GetSemanticPriority(category));
        return true;
    }

    private static IReadOnlyList<OmissionReason> CreateOmissions(
        BudgetExhaustionReason exhaustionReason)
    {
        if (exhaustionReason == BudgetExhaustionReason.None)
            return Array.Empty<OmissionReason>();

        var description = exhaustionReason switch
        {
            BudgetExhaustionReason.ServerLimitClamped =>
                "The requested perception budget was limited by the environment.",
            BudgetExhaustionReason.CandidateLimitReached =>
                "The candidate exploration budget was exhausted.",
            BudgetExhaustionReason.ObservationLimitReached =>
                "The observation return budget was exhausted.",
            BudgetExhaustionReason.TimeBudgetExhausted =>
                "The perception processing budget was exhausted.",
            _ => "The perception result was truncated by an environment budget.",
        };

        return new[]
        {
            new OmissionReason
            {
                Category = OmissionCategory.BudgetExhausted,
                Description = description,
            },
        };
    }

}
