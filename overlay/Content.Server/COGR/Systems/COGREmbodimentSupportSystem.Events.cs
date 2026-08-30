using System;
using COGR.Contracts.Embodiment;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Time;
using Content.Shared.COGR.Components;
using Content.Shared.Mobs;

namespace Content.Server.COGR.Systems;

public sealed partial class COGREmbodimentSupportSystem
{
    private void SubscribeEmbodimentEvents()
    {
        // Controlled-body ComponentStartup/ComponentShutdown are exclusively owned by
        // COGRBodyAuthorityCoordinatorSystem. Support state itself is event-driven here.
        SubscribeLocalEvent<MobStateChangedEvent>(OnControlledMobStateChanged);
    }

    /// <summary>
    /// Publishes the initial normalized support sample after the authority coordinator has
    /// successfully established the exact connection/body/generation lease.
    /// </summary>
    public void NotifyControlledBodyAuthorityBound(EntityUid uid, COGRControlledComponent controlled)
    {
        PublishCurrentSupport(uid, controlled);
    }

    /// <summary>
    /// Resolves the current normalized operational support for one exact semantic observer scope.
    /// Expensive adapter-side world projection may use this as an embodiment service gate without
    /// learning or duplicating SS14-native MobState policy.
    /// </summary>
    public bool TryGetCurrentOperationalSupport(
        SemanticReplicaScope scope,
        out EmbodimentSupportChannelValue support)
    {
        ArgumentNullException.ThrowIfNull(scope);
        support = EmbodimentSupportChannelValue.Zero;

        var body = _authority.ResolveBoundBody(
            scope.AgentId,
            scope.BodyId,
            scope.ConnectionId,
            scope.BodyGeneration);
        if (!body.HasValue)
            return false;

        support = ResolveOperationalSupport(body.Value);
        return true;
    }

    public void NotifyControlledBodyRemoved(COGRControlledComponent controlled)
    {
        if (controlled.AgentId == Guid.Empty)
            return;

        _published.Remove(AgentId.FromGuid(controlled.AgentId));
    }

    private void OnControlledMobStateChanged(MobStateChangedEvent args)
    {
        if (!TryComp<COGRControlledComponent>(args.Target, out var controlled))
            return;

        PublishCurrentSupport(args.Target, controlled);
    }

    private void PublishCurrentSupport(EntityUid uid, COGRControlledComponent controlled)
    {
        var connection = _adapter.Connection;
        var boundWorld = _authority.BoundWorld;
        var boundConnection = _authority.BoundConnection;
        if (connection is not { IsConnected: true } ||
            connection.ConnectionId == Guid.Empty ||
            !boundWorld.HasValue ||
            !boundConnection.HasValue ||
            controlled.AgentId == Guid.Empty ||
            controlled.BodyId == Guid.Empty ||
            !controlled.IsActive)
        {
            return;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        if (boundConnection.Value != connectionId)
            return;

        var agentId = AgentId.FromGuid(controlled.AgentId);
        var bodyId = BodyId.FromGuid(controlled.BodyId);
        var lease = _authority.ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue ||
            lease.Value.BodyId != bodyId ||
            !_authority.ResolveBoundBody(agentId, bodyId, connectionId, lease.Value.Generation).HasValue)
        {
            return;
        }

        var scope = new EmbodimentSupportAuthorityScope
        {
            ConnectionId = connectionId,
            AgentId = agentId,
            BodyId = bodyId,
            BodyGeneration = lease.Value.Generation,
        };
        var support = ResolveOperationalSupport(uid);
        if (!PublishIfNeeded(
                connection,
                boundWorld.Value,
                scope,
                support,
                new SimTick((ulong)_timing.CurTick.Value)))
        {
            return;
        }

        // A body becoming cognitively serviceable is an explicit semantic wake event. This
        // replaces the old timer retry: one dirty scope is queued, deduplicated, and projected
        // later on the Station update boundary.
        if (support.Units > 0 &&
            EntityManager.TrySystem<COGRSemanticReplicaSystem>(out var semanticReplica))
        {
            semanticReplica.NotifySemanticScopeDirty(
                new SemanticReplicaOwner(connectionId, agentId),
                "embodiment_support_available");
        }
    }
}
