using COGR.Contracts.Messages;
using COGR.Core.Actions;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using COGR.Transport.Grpc.Mapping;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Robust.Shared.Containers;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Evaluates exact-reference contextual affordances without invoking native actions.
/// Station remains authoritative for embodiment, visibility, reach, access, object state,
/// and native availability; only bounded environment-neutral evidence crosses the boundary.
/// </summary>
public sealed partial class COGRContextualAffordanceSystem : EntitySystem
{
    private const float InteractionRange = 1.5f;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _logManager = default!;

    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private COGRSemanticReplicaSystem _replica = default!;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _replica = EntityManager.System<COGRSemanticReplicaSystem>();
        _sawmill = _logManager.GetSawmill("cogr.affordance");
    }

    /// <summary>
    /// Evaluates and publishes one bounded result on the Station main thread.
    /// </summary>
    public void HandleQuery(ContextualAffordanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var connection = _adapter.Connection;
        if (connection is not { IsConnected: true } ||
            connection.ConnectionId == Guid.Empty ||
            ConnectionId.FromGuid(connection.ConnectionId) != query.ConnectionId ||
            !_authority.BoundWorld.HasValue)
        {
            _sawmill.Warning("Dropping contextual affordance query for a stale connection");
            return;
        }

        var result = Evaluate(query);
        connection.EnqueueEnvironmentMessage(new PerceptionMessage
        {
            WorldId = _authority.BoundWorld.Value,
            ConnectionId = query.ConnectionId,
            Tick = result.AssessedAtTick,
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            CorrelationId = CorrelationId.FromGuid(query.QueryId),
            AgentId = query.AgentId,
            PerceptId = PerceptId.FromGuid(query.QueryId),
            Category = PerceptionCategory.Environmental,
            Data = ContextualAffordanceWireCodec.EncodeResult(result),
            Format = ContextualAffordanceWireCodec.ResultFormat,
        });

        _sawmill.Debug(
            "Contextual affordance result: query={0}, agent={1}, generation={2}, capability={3}, disposition={4}, blocker={5}",
            query.QueryId,
            query.AgentId,
            query.BodyGeneration,
            query.Capability.ToActionTypeString(),
            result.Disposition,
            result.BlockingReason);
    }

    private ContextualAffordanceResult Evaluate(ContextualAffordanceQuery query)
    {
        var tick = new SimTick((ulong)_timing.CurTick.Value);
        var lease = _authority.ResolveBoundLease(query.AgentId, query.ConnectionId);
        if (!lease.HasValue ||
            lease.Value.BodyId != query.BodyId ||
            lease.Value.Generation != query.BodyGeneration)
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Stale,
                ContextualAffordanceBlockingReason.AuthorityChanged);
        }

        var actor = _authority.ResolveBoundBody(
            query.AgentId,
            query.BodyId,
            query.ConnectionId,
            query.BodyGeneration);
        if (!actor.HasValue)
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Stale,
                ContextualAffordanceBlockingReason.AuthorityChanged);
        }

        if (!_replica.TryGetCurrentObservation(query, out _))
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Unknown,
                ContextualAffordanceBlockingReason.NotObservable);
        }

        var registry = _adapter.ReferenceRegistry;
        if (registry == null)
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Stale,
                ContextualAffordanceBlockingReason.StaleReference);
        }

        var target = registry.TryResolve(
            query.EnvironmentReference,
            new EnvironmentReferenceResolutionContext
            {
                ConnectionId = query.ConnectionId,
                CurrentTick = tick,
                BodyId = query.BodyId,
                BodyGeneration = query.BodyGeneration,
            });
        if (!target.HasValue)
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Stale,
                ContextualAffordanceBlockingReason.StaleReference);
        }

        if (!EntityManager.TrySystem<SharedInteractionSystem>(out var interactionSystem))
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Unknown,
                ContextualAffordanceBlockingReason.MissingCapability);
        }

        if (!TryComp(actor.Value, out TransformComponent? actorTransform) ||
            !TryComp(target.Value, out TransformComponent? targetTransform) ||
            !actorTransform.Coordinates.TryDistance(
                EntityManager,
                targetTransform.Coordinates,
                out var distance))
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Unknown,
                ContextualAffordanceBlockingReason.NotCurrentlyAvailable);
        }

        if (distance > InteractionRange)
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Blocked,
                ContextualAffordanceBlockingReason.OutOfReach);
        }

        if (!interactionSystem.InRangeUnobstructed(actor.Value, target.Value, InteractionRange))
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Blocked,
                ContextualAffordanceBlockingReason.Obstructed);
        }

        return query.Capability switch
        {
            ActionCapability.InteractionOpen => EvaluateDoorOpen(query, actor.Value, target.Value, tick),
            ActionCapability.InteractionClose => EvaluateDoorClose(query, actor.Value, target.Value, tick),
            ActionCapability.ManipulationPickUp => EvaluatePickup(query, actor.Value, target.Value, tick),
            _ => Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Unknown,
                ContextualAffordanceBlockingReason.MissingCapability),
        };
    }

    private ContextualAffordanceResult EvaluateDoorOpen(
        ContextualAffordanceQuery query,
        EntityUid actor,
        EntityUid target,
        SimTick tick)
    {
        if (!TryComp<DoorComponent>(target, out var door) ||
            !EntityManager.TrySystem<SharedDoorSystem>(out var doorSystem))
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Unknown,
                ContextualAffordanceBlockingReason.MissingCapability);
        }

        if (door.State == DoorState.Open)
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Blocked,
                ContextualAffordanceBlockingReason.WrongState);
        }

        return doorSystem.CanOpen(target, door, actor, quiet: true)
            ? Available(query, tick)
            : Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Blocked,
                ContextualAffordanceBlockingReason.NotCurrentlyAvailable);
    }

    private ContextualAffordanceResult EvaluateDoorClose(
        ContextualAffordanceQuery query,
        EntityUid actor,
        EntityUid target,
        SimTick tick)
    {
        if (!TryComp<DoorComponent>(target, out var door) ||
            !EntityManager.TrySystem<SharedDoorSystem>(out var doorSystem))
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Unknown,
                ContextualAffordanceBlockingReason.MissingCapability);
        }

        if (door.State == DoorState.Closed)
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Blocked,
                ContextualAffordanceBlockingReason.WrongState);
        }

        return doorSystem.CanClose(target, door, actor)
            ? Available(query, tick)
            : Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Blocked,
                ContextualAffordanceBlockingReason.NotCurrentlyAvailable);
    }

    private ContextualAffordanceResult EvaluatePickup(
        ContextualAffordanceQuery query,
        EntityUid actor,
        EntityUid target,
        SimTick tick)
    {
        if (!TryComp<ItemComponent>(target, out var item) ||
            !TryComp<HandsComponent>(actor, out var hands) ||
            !EntityManager.TrySystem<SharedHandsSystem>(out var handsSystem))
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Unknown,
                ContextualAffordanceBlockingReason.MissingCapability);
        }

        if (EntityManager.TrySystem<SharedContainerSystem>(out var containerSystem) &&
            containerSystem.TryGetContainingContainer((target, null, null), out _))
        {
            return Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Unknown,
                ContextualAffordanceBlockingReason.NotObservable);
        }

        return handsSystem.CanPickupAnyHand(
                actor,
                target,
                checkActionBlocker: true,
                showPopup: false,
                handsComp: hands,
                item: item)
            ? Available(query, tick)
            : Unavailable(
                query,
                tick,
                ContextualAffordanceDisposition.Blocked,
                ContextualAffordanceBlockingReason.NotCurrentlyAvailable);
    }

    private static ContextualAffordanceResult Available(
        ContextualAffordanceQuery query,
        SimTick tick) => new()
    {
        QueryId = query.QueryId,
        ConnectionId = query.ConnectionId,
        AgentId = query.AgentId,
        BodyId = query.BodyId,
        BodyGeneration = query.BodyGeneration,
        EnvironmentReference = query.EnvironmentReference,
        Capability = query.Capability,
        CausalTraceId = query.CausalTraceId,
        Disposition = ContextualAffordanceDisposition.Available,
        BlockingReason = ContextualAffordanceBlockingReason.None,
        AssessedAtTick = tick,
        ValidThroughTick = new SimTick(tick.Value + query.MaxResultAgeTicks),
    };

    private static ContextualAffordanceResult Unavailable(
        ContextualAffordanceQuery query,
        SimTick tick,
        ContextualAffordanceDisposition disposition,
        ContextualAffordanceBlockingReason reason) => new()
    {
        QueryId = query.QueryId,
        ConnectionId = query.ConnectionId,
        AgentId = query.AgentId,
        BodyId = query.BodyId,
        BodyGeneration = query.BodyGeneration,
        EnvironmentReference = query.EnvironmentReference,
        Capability = query.Capability,
        CausalTraceId = query.CausalTraceId,
        Disposition = disposition,
        BlockingReason = reason,
        AssessedAtTick = tick,
        ValidThroughTick = tick,
    };
}
