using System;
using System.Collections.Generic;
using System.Linq;
using COGR.Contracts.Messages;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using COGR.Transport.Grpc.Mapping;
using Content.Server.COGR;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Maintains bounded observer-scoped semantic projections for active COGR bodies.
/// The Station adapter remains authoritative for visibility, obstruction, body authority,
/// opaque references, and semantic projection; the runtime receives only baselines and diffs.
/// </summary>
public sealed partial class COGRSemanticReplicaSystem : EntitySystem
{
    private const int CandidateBudget = 256;
    private const int ObservationBudget = 64;
    private const int ProcessingBudgetMs = 50;
    private const int MaxPendingResyncRequests = 256;
    private const int MaxPendingDirtyScopes = 4_096;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _logManager = default!;

    private readonly Dictionary<SemanticReplicaOwner, ReplicaState> _replicas = new();
    private readonly Queue<SemanticReplicaResyncRequest> _pendingResyncRequests = new();
    private readonly Queue<PendingDirtyScope> _pendingDirtyScopes = new();
    private readonly HashSet<SemanticReplicaOwner> _pendingDirtyOwners = new();
    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private COGREmbodimentSupportSystem _embodimentSupport = default!;
    private COGRBoundedPerceptionSystem _perception = default!;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(COGRBoundedPerceptionSystem));

        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _embodimentSupport = EntityManager.System<COGREmbodimentSupportSystem>();
        _perception = EntityManager.System<COGRBoundedPerceptionSystem>();
        _sawmill = _logManager.GetSawmill("cogr.replica");
        SubscribeSemanticScopeLifecycle();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var connection = _adapter.Connection;
        if (connection is not { IsConnected: true } ||
            connection.ConnectionId == Guid.Empty ||
            !_authority.BoundWorld.HasValue ||
            !_authority.BoundConnection.HasValue)
        {
            _replicas.Clear();
            _pendingResyncRequests.Clear();
            _pendingDirtyScopes.Clear();
            _pendingDirtyOwners.Clear();
            ClearSemanticScopeCache();
            return;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        if (_authority.BoundConnection.Value != connectionId)
            return;

        RefreshSemanticScopesIfNeeded(connectionId);

        if (_perception.ProjectionPerformedThisTick)
            return;

        if (_pendingResyncRequests.Count > 0)
        {
            ProcessResync(_pendingResyncRequests.Dequeue());
            return;
        }

        if (!TryTakeNextDirtyScope(out var scope, out var reason))
            return;

        var forceBaseline = !_replicas.TryGetValue(scope.Owner, out var state) || state.Scope != scope;
        ProjectAndPublish(scope, forceBaseline, ProjectionOrigin.Auto, reason);
    }

    public void HandleResync(SemanticReplicaResyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = _adapter.Connection;
        if (connection is not { IsConnected: true } ||
            connection.ConnectionId == Guid.Empty ||
            !_authority.BoundConnection.HasValue ||
            _authority.BoundConnection.Value != request.Scope.ConnectionId ||
            ConnectionId.FromGuid(connection.ConnectionId) != request.Scope.ConnectionId)
        {
            _sawmill.Warning("Semantic replica resync ignored: reason=stale_connection");
            return;
        }

        if (_pendingResyncRequests.Count >= MaxPendingResyncRequests)
        {
            _sawmill.Warning(
                "Semantic replica resync dropped: agent={0} reason=queue_full depth={1}",
                request.Scope.AgentId,
                _pendingResyncRequests.Count);
            return;
        }

        _pendingResyncRequests.Enqueue(request);
        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[PROMPTED] replica.resync queued agent={0} generation={1} depth={2}",
                request.Scope.AgentId,
                request.Scope.BodyGeneration,
                _pendingResyncRequests.Count);
        }
    }

    private void ProcessResync(SemanticReplicaResyncRequest request)
    {
        var connection = _adapter.Connection;
        if (connection is not { IsConnected: true } ||
            connection.ConnectionId == Guid.Empty ||
            !_authority.BoundConnection.HasValue ||
            _authority.BoundConnection.Value != request.Scope.ConnectionId ||
            ConnectionId.FromGuid(connection.ConnectionId) != request.Scope.ConnectionId)
        {
            _sawmill.Warning("Queued semantic replica resync ignored: reason=stale_connection");
            return;
        }

        var lease = _authority.ResolveBoundLease(
            request.Scope.AgentId,
            request.Scope.ConnectionId);
        if (!lease.HasValue ||
            lease.Value.BodyId != request.Scope.BodyId ||
            lease.Value.Generation != request.Scope.BodyGeneration)
        {
            _sawmill.Warning(
                "Semantic replica resync ignored: agent={0} generation={1} reason=stale_authority",
                request.Scope.AgentId,
                request.Scope.BodyGeneration);
            return;
        }

        ProjectAndPublish(request.Scope, forceBaseline: true, ProjectionOrigin.Prompted, "runtime_resync");
    }

    public void NotifySemanticScopeDirty(SemanticReplicaOwner owner, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A semantic dirty reason is required.", nameof(reason));

        QueueDirtyScope(owner, reason);
    }

    public bool SkipNextDelta(AgentId agentId)
    {
        foreach (var (owner, state) in _replicas)
        {
            if (owner.AgentId != agentId)
                continue;

            state.SkipNextSequence = true;
            return true;
        }

        return false;
    }

    public IReadOnlyList<SemanticReplicaDiagnosticState> GetDiagnosticStates() =>
        _replicas
            .OrderBy(entry => entry.Key.ConnectionId)
            .ThenBy(entry => entry.Key.AgentId)
            .Select(entry => new SemanticReplicaDiagnosticState(
                entry.Key.ConnectionId,
                entry.Key.AgentId,
                entry.Value.Scope.BodyId,
                entry.Value.Scope.BodyGeneration,
                entry.Value.Sequence,
                entry.Value.Observations.Count,
                entry.Value.SkipNextSequence))
            .ToArray();

    /// <summary>
    /// Returns the adapter's exact current bounded visual replica for one action authority scope. This is a Station-only
    /// diagnostic read: opaque references and adapter observations never flow back into COGR cognition through this API.
    /// </summary>
    public IReadOnlyList<Observation> GetCurrentObservationsForDiagnostic(
        ConnectionId connectionId,
        AgentId agentId,
        BodyId bodyId,
        uint bodyGeneration)
    {
        var owner = new SemanticReplicaOwner(connectionId, agentId);
        if (!_replicas.TryGetValue(owner, out var state)
            || state.Scope.ConnectionId != connectionId
            || state.Scope.AgentId != agentId
            || state.Scope.BodyId != bodyId
            || state.Scope.BodyGeneration != bodyGeneration)
        {
            return Array.Empty<Observation>();
        }

        return state.Observations.Values
            .OrderBy(static observation => observation.EnvironmentRef)
            .ToArray();
    }

    private void ProjectAndPublish(
        SemanticReplicaScope scope,
        bool forceBaseline,
        ProjectionOrigin origin,
        string reason)
    {
        var connection = _adapter.Connection;
        if (connection is not { IsConnected: true } ||
            !_authority.BoundWorld.HasValue)
        {
            return;
        }

        var traceEnabled = COGRAdapterTrace.Enabled;
        var originTag = origin == ProjectionOrigin.Prompted ? "[PROMPTED]" : "[AUTO]";
        var observer = _authority.ResolveBoundBody(
            scope.AgentId,
            scope.BodyId,
            scope.ConnectionId,
            scope.BodyGeneration);
        if (!observer.HasValue)
        {
            if (traceEnabled)
            {
                _sawmill.Info(
                    "{0} replica.project skipped agent={1} reason={2} cause=no_current_body",
                    originTag,
                    scope.AgentId,
                    reason);
            }

            return;
        }

        if (!_embodimentSupport.TryGetCurrentOperationalSupport(scope, out var support) ||
            support.Units == 0)
        {
            if (traceEnabled)
            {
                _sawmill.Info(
                    "{0} replica.project skipped agent={1} reason={2} cause=no_operational_support",
                    originTag,
                    scope.AgentId,
                    reason);
            }

            return;
        }

        var tick = new SimTick((ulong)_timing.CurTick.Value);
        var request = CreateProjectionRequest(scope, tick);
        var stopwatch = traceEnabled
            ? System.Diagnostics.Stopwatch.StartNew()
            : null;
        var result = _perception.ProjectReplica(request, observer.Value, tick);
        stopwatch?.Stop();

        if (traceEnabled)
        {
            _sawmill.Info(
                "{0} replica.project agent={1} reason={2} baseline={3} observations={4} state={5} elapsed_ms={6:F2}",
                originTag,
                scope.AgentId,
                reason,
                forceBaseline,
                result.Observations.Count,
                result.CompletionState,
                stopwatch!.Elapsed.TotalMilliseconds);
        }

        if (result.CompletionState == PerceptionCompletionState.Failed)
        {
            _sawmill.Warning(
                "Semantic replica projection failed: agent={0} reason={1} detail={2}",
                scope.AgentId,
                reason,
                result.Omissions.FirstOrDefault()?.Description ?? "unspecified failure");
            return;
        }

        var projected = result.Observations.ToDictionary(
            observation => observation.EnvironmentRef,
            observation => observation);
        var owner = scope.Owner;

        if (!_replicas.TryGetValue(owner, out var state) ||
            state.Scope != scope ||
            forceBaseline)
        {
            var baselineSequence = state == null || state.Scope != scope
                ? ObserverReplicaSequence.First
                : state.Sequence.Next();
            PublishBaseline(scope, baselineSequence, tick, projected.Values, originTag, reason, traceEnabled);
            _replicas[owner] = new ReplicaState(scope, baselineSequence, projected);
            return;
        }

        var nextObservations = new Dictionary<EnvironmentRef, Observation>(projected.Count);
        var changes = new List<SemanticReplicaChange>();
        foreach (var (environmentReference, observation) in projected)
        {
            if (state.Observations.TryGetValue(environmentReference, out var previous) &&
                SemanticallyEquivalent(previous, observation))
            {
                nextObservations[environmentReference] = previous;
                continue;
            }

            nextObservations[environmentReference] = observation;
            changes.Add(SemanticReplicaChange.Upsert(observation));
        }

        foreach (var environmentReference in state.Observations.Keys)
        {
            if (!projected.ContainsKey(environmentReference))
            {
                changes.Add(SemanticReplicaChange.Remove(
                    environmentReference,
                    "no_longer_observed"));
            }
        }

        if (changes.Count == 0)
            return;

        if (state.SkipNextSequence)
        {
            state.Sequence = state.Sequence.Next();
            state.SkipNextSequence = false;
            if (traceEnabled)
            {
                _sawmill.Info(
                    "[AUTO] replica.sequence_gap injected agent={0}",
                    scope.AgentId);
            }
        }

        var baseSequence = state.Sequence;
        var sequence = baseSequence.Next();
        var delta = new SemanticReplicaDelta
        {
            Scope = scope,
            BaseSequence = baseSequence,
            Sequence = sequence,
            ObservedAtTick = tick,
            Changes = changes,
        };

        connection.EnqueueEnvironmentMessage(new PerceptionMessage
        {
            WorldId = _authority.BoundWorld.Value,
            ConnectionId = scope.ConnectionId,
            Tick = tick,
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            AgentId = scope.AgentId,
            PerceptId = PerceptId.NewId(),
            Category = PerceptionCategory.Environmental,
            Data = SemanticReplicaWireCodec.EncodeDelta(delta),
            Format = SemanticReplicaWireCodec.DeltaFormat,
        });

        state.Sequence = sequence;
        state.Observations = nextObservations;
        if (traceEnabled)
        {
            _sawmill.Info(
                "{0} replica.delta agent={1} reason={2} sequence={3} changes={4}",
                originTag,
                scope.AgentId,
                reason,
                sequence,
                changes.Count);
        }
    }

    private void PublishBaseline(
        SemanticReplicaScope scope,
        ObserverReplicaSequence sequence,
        SimTick tick,
        IEnumerable<Observation> observations,
        string originTag,
        string reason,
        bool traceEnabled)
    {
        var connection = _adapter.Connection;
        if (connection is not { IsConnected: true } ||
            !_authority.BoundWorld.HasValue)
        {
            return;
        }

        var ordered = observations
            .OrderBy(observation => observation.EnvironmentRef)
            .ToArray();
        var baseline = new SemanticReplicaBaseline
        {
            Scope = scope,
            Sequence = sequence,
            ObservedAtTick = tick,
            Observations = ordered,
        };

        connection.EnqueueEnvironmentMessage(new PerceptionMessage
        {
            WorldId = _authority.BoundWorld.Value,
            ConnectionId = scope.ConnectionId,
            Tick = tick,
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            AgentId = scope.AgentId,
            PerceptId = PerceptId.NewId(),
            Category = PerceptionCategory.Environmental,
            Data = SemanticReplicaWireCodec.EncodeBaseline(baseline),
            Format = SemanticReplicaWireCodec.BaselineFormat,
        });

        if (traceEnabled)
        {
            _sawmill.Info(
                "{0} replica.baseline agent={1} reason={2} generation={3} sequence={4} observations={5}",
                originTag,
                scope.AgentId,
                reason,
                scope.BodyGeneration,
                sequence,
                ordered.Length);
        }
    }

    private static PerceptionRequest CreateProjectionRequest(
        SemanticReplicaScope scope,
        SimTick tick)
    {
        return new PerceptionRequest
        {
            RequestId = Guid.CreateVersion7(),
            ConnectionId = scope.ConnectionId,
            AgentId = scope.AgentId,
            BodyId = scope.BodyId,
            BodyGeneration = scope.BodyGeneration,
            RequestedAtTick = tick,
            Modality = PerceptionModality.Visual,
            Anchor = AttentionAnchor.Self(scope.BodyId),
            Budget = new PerceptionBudget
            {
                MaxEntitiesConsidered = CandidateBudget,
                MaxObservationsReturned = ObservationBudget,
                MaxDistance = COGRSpatialPolicy.DefaultVisualHorizon,
                MaxTraversalDepth = 1,
                MaxProcessingTimeMs = ProcessingBudgetMs,
                PrioritizeSalience = true,
                SupportContinuation = false,
            },
            SearchConceptHints = Array.Empty<string>(),
            CausalTraceId = CausalTraceId.NewId(),
        };
    }

    private static bool SemanticallyEquivalent(
        Observation left,
        Observation right)
    {
        if (left.EnvironmentRef != right.EnvironmentRef ||
            left.Category != right.Category ||
            left.Location != right.Location ||
            left.Salience != right.Salience ||
            left.Confidence != right.Confidence ||
            left.TemporalQuality != right.TemporalQuality ||
            left.AcquisitionMode != right.AcquisitionMode ||
            !FeaturesEquivalent(left.Features, right.Features) ||
            left.Subreferents.Count != right.Subreferents.Count ||
            left.Relations.Count != right.Relations.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Subreferents.Count; index++)
        {
            var leftSubreferent = left.Subreferents[index];
            var rightSubreferent = right.Subreferents[index];
            if (leftSubreferent.Reference != rightSubreferent.Reference ||
                leftSubreferent.Confidence != rightSubreferent.Confidence ||
                leftSubreferent.Category != rightSubreferent.Category ||
                !FeaturesEquivalent(leftSubreferent.Features, rightSubreferent.Features))
            {
                return false;
            }
        }

        for (var index = 0; index < left.Relations.Count; index++)
        {
            var leftRelation = left.Relations[index];
            var rightRelation = right.Relations[index];
            if (leftRelation.Subject != rightRelation.Subject ||
                leftRelation.RelationType != rightRelation.RelationType ||
                leftRelation.Target != rightRelation.Target ||
                leftRelation.Confidence != rightRelation.Confidence)
            {
                return false;
            }
        }

        return true;
    }

    private static bool FeaturesEquivalent(
        IReadOnlyList<ObservedFeature> left,
        IReadOnlyList<ObservedFeature> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var leftFeature = left[index];
            var rightFeature = right[index];
            if (leftFeature.Category != rightFeature.Category ||
                leftFeature.FeatureType != rightFeature.FeatureType ||
                leftFeature.Confidence != rightFeature.Confidence ||
                !Equals(leftFeature.Value, rightFeature.Value))
            {
                return false;
            }
        }

        return true;
    }

    private enum ProjectionOrigin
    {
        Auto,
        Prompted,
    }

    private readonly record struct PendingDirtyScope(
        SemanticReplicaOwner Owner,
        string Reason);

    private sealed class ReplicaState
    {
        public ReplicaState(
            SemanticReplicaScope scope,
            ObserverReplicaSequence sequence,
            Dictionary<EnvironmentRef, Observation> observations)
        {
            Scope = scope;
            Sequence = sequence;
            Observations = observations;
        }

        public SemanticReplicaScope Scope { get; }
        public ObserverReplicaSequence Sequence { get; set; }
        public Dictionary<EnvironmentRef, Observation> Observations { get; set; }
        public bool SkipNextSequence { get; set; }
    }
}

public readonly record struct SemanticReplicaDiagnosticState(
    ConnectionId ConnectionId,
    AgentId AgentId,
    BodyId BodyId,
    uint BodyGeneration,
    ObserverReplicaSequence Sequence,
    int ObservationCount,
    bool SkipNextSequence);