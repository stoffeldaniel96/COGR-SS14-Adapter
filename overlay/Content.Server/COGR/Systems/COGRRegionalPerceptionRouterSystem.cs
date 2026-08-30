using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using Content.Shared.COGR.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Maintains a coarse spatial subscription index for active COGR observers and turns generic
/// authoritative world-change events into observer-scoped perceptual availability.
/// </summary>
/// <remarks>
/// This is an event router, not a perception scheduler and not a semantic classifier. Entity
/// initialization, transform/reparent movement, generic entity dirtiness, and termination are only
/// privileged adapter wake hints. Potentially visible source changes are exposed as cheap passive cues;
/// the bounded actor-relative projector remains responsible for any richer semantic inspection.
///
/// Whole-scene semantic projection is deliberately reserved for observer-owned scene sampling and
/// strong authoritative invalidation. External world changes do not continuously refresh every object
/// in the Coggent's visual field. Chunking is only a routing optimization.
/// </remarks>
public sealed partial class COGRRegionalPerceptionRouterSystem : EntitySystem
{
    private const float ChunkSize = 8f;
    private const int VisualNeighborChunkRadius = 2;

    // One meter of observer self-motion is a sparse scene-sampling boundary for visual egomotion and
    // situated-memory formation. External entity movement uses passive source cues instead.
    private const float SemanticMotionCellSize = 1f;
    private const int MaximumRoutingParentDepth = 16;
    private static readonly TimeSpan PassiveCueMinimumInterval = TimeSpan.FromMilliseconds(250);

    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<RegionKey, HashSet<SemanticReplicaOwner>> _subscribers = new();
    private readonly Dictionary<SemanticReplicaOwner, RegionKey> _ownerRegions = new();
    private readonly Dictionary<PassiveCueKey, TimeSpan> _lastPassiveCueAttemptAt = new();

    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private COGRBodyMotionSensationSystem _bodyMotion = default!;
    private COGRBoundedPerceptionSystem _perception = default!;
    private COGRSemanticReplicaSystem _semanticReplica = default!;
    private COGRPassivePerceptionSystem _passivePerception = default!;

    public override void Initialize()
    {
        base.Initialize();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _bodyMotion = EntityManager.System<COGRBodyMotionSensationSystem>();
        _perception = EntityManager.System<COGRBoundedPerceptionSystem>();
        _semanticReplica = EntityManager.System<COGRSemanticReplicaSystem>();
        _passivePerception = EntityManager.System<COGRPassivePerceptionSystem>();

        EntityManager.EntityInitialized += OnEntityInitialized;
        EntityManager.EntityDirtied += OnEntityDirtied;

        // Robust directed component events permit one subscriber for a component/event pair. This
        // router therefore owns controlled-body MoveEvent and fans the raw movement first to passive
        // embodied sensation, then applies its coarser visual-semantic sampling policy below.
        SubscribeLocalEvent<COGRControlledComponent, MoveEvent>(OnControlledBodyMoved);
        SubscribeLocalEvent<TransformComponent, MoveEvent>(OnEntityMoved);

        // Every entity owns MetaDataComponent, so one generic termination subscription closes both
        // reference revocation and retained visual-state invalidation without category-specific hooks.
        SubscribeLocalEvent<MetaDataComponent, EntityTerminatingEvent>(OnEntityTerminating);
    }

    public override void Shutdown()
    {
        EntityManager.EntityInitialized -= OnEntityInitialized;
        EntityManager.EntityDirtied -= OnEntityDirtied;
        _subscribers.Clear();
        _ownerRegions.Clear();
        _lastPassiveCueAttemptAt.Clear();
        base.Shutdown();
    }

    /// <summary>
    /// Reconciles the regional subscriber index from the already event-invalidated semantic scope
    /// cache. This runs only when scope membership or connection authority changes, never per tick.
    /// </summary>
    public void SynchronizeSemanticScopes(IEnumerable<SemanticReplicaScope> scopes)
    {
        var activeOwners = new HashSet<SemanticReplicaOwner>();
        foreach (var scope in scopes)
        {
            activeOwners.Add(scope.Owner);
            var body = _authority.ResolveBoundBody(
                scope.AgentId,
                scope.BodyId,
                scope.ConnectionId,
                scope.BodyGeneration);
            if (!body.HasValue ||
                !TryComp(body.Value, out TransformComponent? transform) ||
                !TryGetRegion(transform, out var region))
            {
                RemoveOwner(scope.Owner);
                continue;
            }

            MoveOwner(scope.Owner, region);
        }

        var staleOwners = new List<SemanticReplicaOwner>();
        foreach (var owner in _ownerRegions.Keys)
        {
            if (!activeOwners.Contains(owner))
                staleOwners.Add(owner);
        }

        foreach (var owner in staleOwners)
            RemoveOwner(owner);
    }

    /// <summary>
    /// Registers or refreshes one observer after an exact body-authority lease has been bound.
    /// </summary>
    public void NotifyControlledBodyAuthorityBound(EntityUid uid, COGRControlledComponent controlled)
    {
        if (!TryGetCurrentOwner(controlled, out var owner))
            return;
        if (!TryComp(uid, out TransformComponent? transform) ||
            !TryGetRegion(transform, out var region))
        {
            RemoveOwner(owner);
            return;
        }

        RemoveOtherAuthorityOwners(owner.AgentId, owner);
        MoveOwner(owner, region);
    }

    /// <summary>
    /// Removes every subscription retained for a controlled body whose authority is ending.
    /// </summary>
    public void NotifyControlledBodyRemoved(COGRControlledComponent controlled)
    {
        if (controlled.AgentId == Guid.Empty)
            return;

        var agentId = AgentId.FromGuid(controlled.AgentId);
        var owners = new List<SemanticReplicaOwner>();
        foreach (var owner in _ownerRegions.Keys)
        {
            if (owner.AgentId == agentId)
                owners.Add(owner);
        }

        foreach (var owner in owners)
            RemoveOwner(owner);
    }

    /// <summary>
    /// Requests a strong local semantic refresh for adapter machinery that is invalidating already-exposed
    /// evidence, such as authoritative entity termination. Ordinary host updates must use passive perceptual
    /// availability instead of calling this method.
    /// </summary>
    public int NotifyLocalSemanticChange(EntityUid source, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A regional semantic-change reason is required.", nameof(reason));
        if (!TryGetRoutingPoint(source, out var region, out var position))
            return 0;

        return PublishAround(
            region,
            position,
            reason,
            passiveSource: null,
            refreshSemanticReplica: true);
    }

    private void NotifyLocalPerceptualChange(EntityUid source, string reason)
    {
        if (!TryGetRoutingPoint(source, out var region, out var position))
            return;

        _ = PublishAround(
            region,
            position,
            reason,
            passiveSource: source,
            refreshSemanticReplica: false);
    }

    private void OnEntityInitialized(Entity<MetaDataComponent> entity)
    {
        if (_ownerRegions.Count == 0)
            return;

        NotifyLocalPerceptualChange(entity.Owner, "entity_initialized");
    }

    private void OnEntityDirtied(Entity<MetaDataComponent> entity)
    {
        if (_ownerRegions.Count == 0)
            return;

        NotifyLocalPerceptualChange(entity.Owner, "entity_state_dirtied");
    }

    private void OnEntityTerminating(
        Entity<MetaDataComponent> entity,
        ref EntityTerminatingEvent args)
    {
        _ = args;
        RemovePassiveSource(entity.Owner);
        _perception.NotifyEntityTerminating(entity.Owner);
    }

    private void OnControlledBodyMoved(
        EntityUid uid,
        COGRControlledComponent controlled,
        ref MoveEvent args)
    {
        // Passive embodied motion observes the raw authoritative movement stream even when the
        // displacement is too small to cross this router's visual-semantic sampling boundary.
        _bodyMotion.NotifyControlledBodyMoved(uid, controlled, ref args);

        if (!TryGetCurrentOwner(controlled, out var owner))
            return;

        if (!TryComp(uid, out TransformComponent? transform) ||
            !TryGetRegion(transform, out var currentRegion))
        {
            RemoveOwner(owner);
            return;
        }

        MoveOwner(owner, currentRegion);

        var previousParent = args.OldPosition.EntityId;
        var currentParent = args.NewPosition.EntityId;
        var meaningfulSelfMotion = previousParent != currentParent
            || CrossedSemanticMotionCell(args.OldPosition.Position, args.NewPosition.Position);
        if (meaningfulSelfMotion)
            _semanticReplica.NotifySemanticScopeDirty(owner, "observer_moved");
    }

    private void OnEntityMoved(
        EntityUid uid,
        TransformComponent component,
        ref MoveEvent args)
    {
        _ = component;
        if (_ownerRegions.Count == 0)
            return;

        var previousParent = args.OldPosition.EntityId;
        var currentParent = args.NewPosition.EntityId;
        if (previousParent == currentParent &&
            !CrossedSemanticMotionCell(args.OldPosition.Position, args.NewPosition.Position))
        {
            return;
        }

        PublishMovementEndpoint(
            uid,
            previousParent,
            args.OldPosition.Position,
            "entity_moved");
        PublishMovementEndpoint(
            uid,
            currentParent,
            args.NewPosition.Position,
            "entity_moved");
    }

    private bool PublishMovementEndpoint(
        EntityUid source,
        EntityUid parent,
        Vector2 localPosition,
        string reason)
    {
        if (!parent.IsValid())
            return false;

        // Direct grid/map coordinates are already in the same coordinate space as observer regions.
        if (HasComp<MapGridComponent>(parent) || HasComp<MapComponent>(parent))
        {
            var directRegion = RegionKey.From(parent, localPosition);
            PublishAround(
                directRegion,
                localPosition,
                reason,
                passiveSource: source,
                refreshSemanticReplica: false);
            return true;
        }

        // Reparenting through hands, inventories, storage, machinery, etc. is routed around the
        // nearest direct world ancestor. This is only a wake location; focused projection reconstructs
        // the actual containment/hold/part relationships from settled authoritative state.
        if (!TryGetRoutingPoint(parent, out var region, out var position))
            return false;

        PublishAround(
            region,
            position,
            reason,
            passiveSource: source,
            refreshSemanticReplica: false);
        return true;
    }

    private bool TryGetCurrentOwner(
        COGRControlledComponent controlled,
        out SemanticReplicaOwner owner)
    {
        owner = default;
        if (!controlled.IsActive ||
            controlled.AgentId == Guid.Empty ||
            !_authority.BoundConnection.HasValue)
        {
            return false;
        }

        var connectionId = _authority.BoundConnection.Value;
        var agentId = AgentId.FromGuid(controlled.AgentId);
        if (!_authority.ResolveBoundLease(agentId, connectionId).HasValue)
            return false;

        owner = new SemanticReplicaOwner(connectionId, agentId);
        return true;
    }

    private static bool TryGetRegion(TransformComponent transform, out RegionKey region)
    {
        var coordinateSpace = transform.GridUid ?? transform.MapUid;
        if (!coordinateSpace.HasValue)
        {
            region = default;
            return false;
        }

        region = RegionKey.From(coordinateSpace.Value, transform.LocalPosition);
        return true;
    }

    private bool TryGetRoutingPoint(
        EntityUid source,
        out RegionKey region,
        out Vector2 position)
    {
        var current = source;
        for (var depth = 0; depth < MaximumRoutingParentDepth; depth++)
        {
            if (!TryComp(current, out TransformComponent? transform))
                break;

            var coordinateSpace = transform.GridUid ?? transform.MapUid;
            if (!coordinateSpace.HasValue)
                break;

            if (current == coordinateSpace.Value || transform.ParentUid == coordinateSpace.Value)
            {
                region = RegionKey.From(coordinateSpace.Value, transform.LocalPosition);
                position = transform.LocalPosition;
                return true;
            }

            if (!transform.ParentUid.IsValid() || transform.ParentUid == current)
                break;

            current = transform.ParentUid;
        }

        region = default;
        position = default;
        return false;
    }

    private static bool CrossedSemanticMotionCell(Vector2 previous, Vector2 current) =>
        (int)MathF.Floor(previous.X / SemanticMotionCellSize) !=
        (int)MathF.Floor(current.X / SemanticMotionCellSize) ||
        (int)MathF.Floor(previous.Y / SemanticMotionCellSize) !=
        (int)MathF.Floor(current.Y / SemanticMotionCellSize);

    private void MoveOwner(SemanticReplicaOwner owner, RegionKey next)
    {
        if (_ownerRegions.TryGetValue(owner, out var previous))
        {
            if (previous == next)
                return;

            if (_subscribers.TryGetValue(previous, out var previousSubscribers))
            {
                previousSubscribers.Remove(owner);
                if (previousSubscribers.Count == 0)
                    _subscribers.Remove(previous);
            }
        }

        if (!_subscribers.TryGetValue(next, out var nextSubscribers))
        {
            nextSubscribers = new HashSet<SemanticReplicaOwner>();
            _subscribers.Add(next, nextSubscribers);
        }

        nextSubscribers.Add(owner);
        _ownerRegions[owner] = next;
    }

    private void RemoveOwner(SemanticReplicaOwner owner)
    {
        RemovePassiveOwner(owner);

        if (!_ownerRegions.Remove(owner, out var region))
            return;

        if (!_subscribers.TryGetValue(region, out var subscribers))
            return;

        subscribers.Remove(owner);
        if (subscribers.Count == 0)
            _subscribers.Remove(region);
    }

    private void RemoveOtherAuthorityOwners(AgentId agentId, SemanticReplicaOwner keep)
    {
        var stale = new List<SemanticReplicaOwner>();
        foreach (var owner in _ownerRegions.Keys)
        {
            if (owner.AgentId == agentId && !owner.Equals(keep))
                stale.Add(owner);
        }

        foreach (var owner in stale)
            RemoveOwner(owner);
    }

    private int PublishAround(
        RegionKey center,
        Vector2 sourcePosition,
        string reason,
        EntityUid? passiveSource,
        bool refreshSemanticReplica)
    {
        var notified = 0;
        for (var x = center.X - VisualNeighborChunkRadius; x <= center.X + VisualNeighborChunkRadius; x++)
        {
            for (var y = center.Y - VisualNeighborChunkRadius; y <= center.Y + VisualNeighborChunkRadius; y++)
            {
                var candidate = new RegionKey(center.CoordinateSpace, x, y);
                if (!_subscribers.TryGetValue(candidate, out var owners))
                    continue;

                foreach (var owner in owners)
                {
                    if (!IsObserverWithinVisualHorizon(owner, center.CoordinateSpace, sourcePosition))
                        continue;

                    if (refreshSemanticReplica)
                        _semanticReplica.NotifySemanticScopeDirty(owner, reason);
                    if (passiveSource.HasValue)
                        TryPublishPassiveVisualCue(owner, passiveSource.Value, reason);
                    notified++;
                }
            }
        }

        return notified;
    }

    private void TryPublishPassiveVisualCue(
        SemanticReplicaOwner owner,
        EntityUid source,
        string reason)
    {
        var key = new PassiveCueKey(owner, source);
        var now = _timing.CurTime;
        if (_lastPassiveCueAttemptAt.TryGetValue(key, out var previous)
            && now - previous < PassiveCueMinimumInterval)
        {
            return;
        }

        // Throttle the visibility probe itself, not just successful emissions. A continuously dirtied
        // but occluded entity must not turn cheap passive availability into repeated raycast work.
        _lastPassiveCueAttemptAt[key] = now;
        _ = _passivePerception.TryPublishVisualSourceCue(
            owner,
            source,
            ToPassiveEventClass(reason),
            ToPassiveSalience(reason));
    }

    private static string ToPassiveEventClass(string reason) => reason switch
    {
        "entity_initialized" => "visual_source_appeared",
        "entity_moved" => "visual_source_moved",
        "entity_state_dirtied" => "visual_source_changed",
        _ => "visual_source_changed",
    };

    private static double ToPassiveSalience(string reason) => reason switch
    {
        "entity_initialized" => 0.55,
        "entity_moved" => 0.50,
        "entity_state_dirtied" => 0.35,
        _ => 0.40,
    };

    private void RemovePassiveOwner(SemanticReplicaOwner owner)
    {
        foreach (var key in _lastPassiveCueAttemptAt.Keys
                     .Where(key => key.Owner == owner)
                     .ToArray())
        {
            _lastPassiveCueAttemptAt.Remove(key);
        }
    }

    private void RemovePassiveSource(EntityUid source)
    {
        foreach (var key in _lastPassiveCueAttemptAt.Keys
                     .Where(key => key.Source == source)
                     .ToArray())
        {
            _lastPassiveCueAttemptAt.Remove(key);
        }
    }

    private bool IsObserverWithinVisualHorizon(
        SemanticReplicaOwner owner,
        EntityUid coordinateSpace,
        Vector2 sourcePosition)
    {
        var lease = _authority.ResolveBoundLease(owner.AgentId, owner.ConnectionId);
        if (!lease.HasValue)
            return false;

        var body = _authority.ResolveBoundBody(
            owner.AgentId,
            lease.Value.BodyId,
            owner.ConnectionId,
            lease.Value.Generation);
        if (!body.HasValue ||
            !TryGetRoutingPoint(body.Value, out var observerRegion, out var observerPosition) ||
            observerRegion.CoordinateSpace != coordinateSpace)
        {
            return false;
        }

        return Vector2.DistanceSquared(observerPosition, sourcePosition) <=
               COGRSpatialPolicy.DefaultVisualHorizon * COGRSpatialPolicy.DefaultVisualHorizon;
    }

    private readonly record struct PassiveCueKey(
        SemanticReplicaOwner Owner,
        EntityUid Source);

    private readonly record struct RegionKey(EntityUid CoordinateSpace, int X, int Y)
    {
        public static RegionKey From(EntityUid coordinateSpace, Vector2 position) =>
            new(
                coordinateSpace,
                (int)MathF.Floor(position.X / ChunkSize),
                (int)MathF.Floor(position.Y / ChunkSize));
    }
}
