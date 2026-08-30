using System;
using COGR.Contracts.Messages;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using Content.Server.COGR.Systems;

namespace Content.Server.COGR;

public sealed partial class COGRAdapterSystem
{
    private COGRBoundedPerceptionSystem? _boundedPerceptionSystem;
    private COGRSemanticReplicaSystem? _semanticReplicaSystem;
    private COGRContextualAffordanceSystem? _contextualAffordanceSystem;

    private void InitializePerceptionRouting()
    {
        _boundedPerceptionSystem = EntityManager.System<COGRBoundedPerceptionSystem>();
        _semanticReplicaSystem = EntityManager.System<COGRSemanticReplicaSystem>();
        _contextualAffordanceSystem = EntityManager.System<COGRContextualAffordanceSystem>();
        if (Connection == null)
            return;

        Connection.PerceptionRequestReceived -= HandleRuntimePerceptionRequest;
        Connection.PerceptionRequestReceived += HandleRuntimePerceptionRequest;
        Connection.ContextualAffordanceRequested -= HandleContextualAffordanceQuery;
        Connection.ContextualAffordanceRequested += HandleContextualAffordanceQuery;
        Connection.SemanticReplicaResyncRequested -= HandleSemanticReplicaResync;
        Connection.SemanticReplicaResyncRequested += HandleSemanticReplicaResync;
        _sawmill.Info(
            "Configured F3 bounded perception, contextual affordance, and semantic replica routing on the Station main thread");
    }

    private void ShutdownPerceptionRouting()
    {
        if (_boundedPerceptionSystem != null &&
            Connection is { IsConnected: true } connection &&
            connection.ConnectionId != Guid.Empty)
        {
            _boundedPerceptionSystem.InvalidateConnection(
                ConnectionId.FromGuid(connection.ConnectionId),
                "connection_closing");

            // Give the single connection writer one final main-thread opportunity to map and
            // transmit typed invalidations before the duplex stream is closed.
            connection.ProcessPendingMessages();
        }

        if (Connection != null)
        {
            Connection.PerceptionRequestReceived -= HandleRuntimePerceptionRequest;
            Connection.ContextualAffordanceRequested -= HandleContextualAffordanceQuery;
            Connection.SemanticReplicaResyncRequested -= HandleSemanticReplicaResync;
        }

        _boundedPerceptionSystem = null;
        _semanticReplicaSystem = null;
        _contextualAffordanceSystem = null;
    }

    private void HandleRuntimePerceptionRequest(PerceptionRequestMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[PROMPTED] perception.request agent={0} request={1} modality={2}",
                message.Request.AgentId,
                message.Request.RequestId,
                message.Request.Modality);
        }

        if (_boundedPerceptionSystem == null)
        {
            _sawmill.Warning(
                "Perception request dropped: request={0} reason=routing_unavailable",
                message.Request.RequestId);
            return;
        }

        _boundedPerceptionSystem.HandleRequest(message);
    }

    private void HandleContextualAffordanceQuery(ContextualAffordanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[PROMPTED] affordance.query agent={0} query={1}",
                query.AgentId,
                query.QueryId);
        }

        if (_contextualAffordanceSystem == null)
        {
            _sawmill.Warning(
                "Contextual affordance query dropped: query={0} reason=routing_unavailable",
                query.QueryId);
            return;
        }

        _contextualAffordanceSystem.HandleQuery(query);
    }

    private void HandleSemanticReplicaResync(SemanticReplicaResyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[PROMPTED] replica.resync.request agent={0} generation={1}",
                request.Scope.AgentId,
                request.Scope.BodyGeneration);
        }

        if (_semanticReplicaSystem == null)
        {
            _sawmill.Warning(
                "Semantic replica resync request dropped: agent={0} reason=routing_unavailable",
                request.Scope.AgentId);
            return;
        }

        _semanticReplicaSystem.HandleResync(request);
    }
}
