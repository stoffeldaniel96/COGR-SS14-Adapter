using System.Linq;
using System.Threading.Channels;
using COGR.Contracts.Messages;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Time;
using COGR.SS14Bridge;
using Content.Server.COGR.Actions;
using Content.Shared.COGR.Components;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Owns controlled-body startup and shutdown, reconciles body authority with the active COGR
/// connection, and routes shared bridge actions into the SS14 executor.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="COGRBodyRegistrationSystem"/> assigns stable agent and body identities during
/// component initialization. This coordinator is the single subscriber for
/// <see cref="COGRControlledComponent"/> startup and shutdown so embodiment setup, adapter
/// registration cleanup, authority rotation, and action termination occur in one ordered path.
/// </para>
/// <para>
/// The coordinator implements <see cref="IActionAuthorityResolver"/>. The shared bridge receives
/// world, connection, tick, authority, and executor context from SS14 and never invents them.
/// </para>
/// <para>
/// Connection replacement, disconnect, and body shutdown invalidate perception references,
/// rotate the executor's authority generation, and remove the binding from the resolvable
/// context. Accepted actions terminate before bridge context is cleared so the runtime does not
/// retain proposals or environment references after Station authority has ended.
/// </para>
/// </remarks>
public sealed partial class COGRBodyAuthorityCoordinatorSystem : EntitySystem, IActionAuthorityResolver
{
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    private COGRAdapterSystem _adapter = default!;
    private COGRActionExecutor _executor = default!;
    private COGRBodyBindingIndexSystem _bodyIndex = default!;
    private ActionBridge _bridge = default!;
    private ISawmill _sawmill = default!;
    private WorldId? _boundWorld;
    private ConnectionId? _boundConnection;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("cogr.authority");
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _executor = EntityManager.System<COGRActionExecutor>();
        _bodyIndex = EntityManager.System<COGRBodyBindingIndexSystem>();

        var bridgeMessages = Channel.CreateBounded<EnvironmentMessage>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        _bridge = new ActionBridge(bridgeMessages, new COGRSawmillLogger(_sawmill));
        _bridge.RegisterActionAuthorityResolver(this);
        _bridge.RegisterActionProposalHandler(OnActionProposalReceived);
        _adapter.AttachActionBridge(_bridge, bridgeMessages);

        SubscribeLocalEvent<COGRControlledComponent, ComponentStartup>(OnBodyStartup);
        SubscribeLocalEvent<COGRControlledComponent, ComponentShutdown>(OnBodyShutdown);

        _sawmill.Info("Configured Station controlled-body lifecycle, action bridge, and authority routing");
    }

    public override void Shutdown()
    {
        if (_boundConnection.HasValue)
        {
            var connectionId = _boundConnection.Value;
            RotateAllBodies(
                connectionId,
                ActionFailureReason.ConnectionLost,
                "Station authority coordinator shut down");
            InvalidateConnectionReferences(connectionId, "connection_closing");
            _bridge.ClearEnvironmentContext(connectionId);
        }

        _adapter.DetachActionBridge(_bridge);
        _boundWorld = null;
        _boundConnection = null;

        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var activeContext = GetActiveContext();
        if (activeContext.HasValue &&
            _boundWorld.HasValue &&
            _boundConnection.HasValue &&
            _boundWorld.Value == activeContext.Value.WorldId &&
            _boundConnection.Value == activeContext.Value.ConnectionId)
        {
            return;
        }

        if (_boundConnection.HasValue)
        {
            var previousConnection = _boundConnection.Value;
            RotateAllBodies(
                previousConnection,
                ActionFailureReason.ConnectionLost,
                "COGR connection ended or was replaced");
            InvalidateConnectionReferences(previousConnection, "connection_replaced");
            _bridge.ClearEnvironmentContext(previousConnection);
            _boundWorld = null;
            _boundConnection = null;
        }

        if (!activeContext.HasValue)
            return;

        var (worldId, connectionId) = activeContext.Value;
        _boundWorld = worldId;
        _boundConnection = connectionId;

        BindAllBodies(connectionId);
        _bridge.ConfigureEnvironmentContext(
            worldId,
            connectionId,
            GetCurrentTick);

        _sawmill.Info(
            "Configured COGR action bridge for world {0}, connection {1}",
            worldId,
            connectionId);
    }

    /// <summary>
    /// Gets the world currently owning SS14 action routing, if any.
    /// </summary>
    public WorldId? BoundWorld => _boundWorld;

    /// <summary>
    /// Gets the connection currently owning SS14 body authority, if any.
    /// </summary>
    public ConnectionId? BoundConnection => _boundConnection;

    /// <summary>
    /// Resolves bridge authority only when the proposal matches the exact active Station
    /// world and connection.
    /// </summary>
    public ActionAuthorityResolution Resolve(
        WorldId worldId,
        ConnectionId connectionId,
        AgentId agentId,
        SimTick proposedAtTick)
    {
        _ = proposedAtTick;

        if (!_boundWorld.HasValue ||
            !_boundConnection.HasValue ||
            _boundWorld.Value != worldId ||
            _boundConnection.Value != connectionId)
        {
            return ActionAuthorityResolution.Rejected(
                ActionRejectionReason.ConnectionNotAuthorized,
                "Proposal world or connection does not match the active Station context");
        }

        var lease = ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue)
        {
            return ActionAuthorityResolution.Rejected(
                ActionRejectionReason.NoBodyAuthority,
                "No unambiguous current SS14 body authority exists for this agent");
        }

        return ActionAuthorityResolution.Resolved(lease.Value);
    }

    /// <summary>
    /// Resolves the single current body lease for an agent under the active connection.
    /// </summary>
    /// <remarks>
    /// Identity membership is maintained by lifecycle events. Duplicate agent identities remain
    /// fail-closed because the body index only resolves an agent when exactly one entity is bound.
    /// </remarks>
    public BodyAuthorityLease? ResolveBoundLease(
        AgentId agentId,
        ConnectionId connectionId)
    {
        if (!_boundConnection.HasValue || _boundConnection.Value != connectionId)
            return null;

        if (!_bodyIndex.TryGetUniqueEntity(agentId, out var uid) ||
            !TryComp<COGRControlledComponent>(uid, out var component) ||
            component.AgentId == Guid.Empty ||
            component.BodyId == Guid.Empty ||
            AgentId.FromGuid(component.AgentId) != agentId)
        {
            return null;
        }

        var bodyId = BodyId.FromGuid(component.BodyId);
        var lease = _executor.GetBodyAuthority(bodyId);
        if (!lease.HasValue ||
            !lease.Value.IsValid ||
            lease.Value.AgentId != agentId ||
            lease.Value.BodyId != bodyId ||
            lease.Value.ConnectionId != connectionId ||
            lease.Value.Generation == 0)
        {
            return null;
        }

        return lease;
    }

    /// <summary>
    /// Resolves one exact controlled body only when the request matches the current
    /// connection-scoped authority generation.
    /// </summary>
    public EntityUid? ResolveBoundBody(
        AgentId agentId,
        BodyId bodyId,
        ConnectionId connectionId,
        uint generation)
    {
        if (!_boundConnection.HasValue ||
            _boundConnection.Value != connectionId ||
            generation == 0)
        {
            return null;
        }

        if (!_bodyIndex.TryGetUniqueEntity(bodyId, out var uid) ||
            !TryComp<COGRControlledComponent>(uid, out var component) ||
            component.AgentId == Guid.Empty ||
            component.BodyId == Guid.Empty ||
            AgentId.FromGuid(component.AgentId) != agentId ||
            BodyId.FromGuid(component.BodyId) != bodyId)
        {
            return null;
        }

        var lease = _executor.GetBodyAuthority(bodyId);
        if (!lease.HasValue ||
            !lease.Value.IsValid ||
            lease.Value.AgentId != agentId ||
            lease.Value.BodyId != bodyId ||
            lease.Value.ConnectionId != connectionId ||
            lease.Value.Generation != generation)
        {
            return null;
        }

        return uid;
    }

    private void OnActionProposalReceived(ActionAttempt attempt)
    {
        var proposalResult = _executor.ProposeAction(attempt);
        _bridge.OnActionDisposition(
            attempt.ProposalId,
            proposalResult.IsAccepted,
            proposalResult.RejectionReason,
            proposalResult.Detail);

        if (!proposalResult.IsAccepted)
            return;

        var cancellationTargets = GetCancellationTargets(attempt);
        var executionResult = _executor.StartAction(attempt.ProposalId);
        var tick = GetCurrentTick();

        if (!executionResult.IsSuccess)
        {
            _bridge.OnActionTerminalResult(ActionResult.Failed(
                attempt.ProposalId,
                tick,
                executionResult.FailureReason ?? ActionFailureReason.Unspecified,
                executionResult.Detail));
            return;
        }

        foreach (var target in cancellationTargets)
        {
            _executor.CleanupActionTracking(target.ProposalId, target.BodyId);
            _bridge.OnActionTerminalResult(ActionResult.Cancelled(
                target.ProposalId,
                tick,
                $"Cancelled by action {attempt.ProposalId}"));
        }

        if (!executionResult.IsStarted)
        {
            _bridge.OnActionTerminalResult(ActionResult.Completed(
                attempt.ProposalId,
                tick,
                executionResult.ResultData));
        }
    }

    private IReadOnlyList<ActionAttempt> GetCancellationTargets(ActionAttempt attempt)
    {
        if (attempt.Capability == ActionCapability.MovementStop)
        {
            return _executor.ActionRegistry
                .GetActiveForBody(attempt.BodyId)
                .Where(active =>
                    active.ProposalId != attempt.ProposalId &&
                    active.Capability.GetCategory() == "movement")
                .ToList();
        }

        if (attempt.Capability != ActionCapability.ActionCancel)
            return Array.Empty<ActionAttempt>();

        var parameters = ActionParameterSerializer.Deserialize<CancelActionParams>(attempt.Parameters);
        if (parameters == null)
            return Array.Empty<ActionAttempt>();

        var target = _executor.ActionRegistry.GetAction(parameters.TargetProposalId);
        return target == null
            ? Array.Empty<ActionAttempt>()
            : new[] { target };
    }

    private (WorldId WorldId, ConnectionId ConnectionId)? GetActiveContext()
    {
        if (!_adapter.IsConnected || _adapter.Connection == null)
            return null;

        var rawWorldId = _adapter.Connection.WorldId;
        var rawConnectionId = _adapter.Connection.ConnectionId;
        if (rawWorldId == Guid.Empty || rawConnectionId == Guid.Empty)
            return null;

        return (
            WorldId.FromGuid(rawWorldId),
            ConnectionId.FromGuid(rawConnectionId));
    }

    private SimTick GetCurrentTick() => new((ulong)_timing.CurTick.Value);

    private void OnBodyStartup(
        EntityUid uid,
        COGRControlledComponent component,
        ComponentStartup args)
    {
        _sawmill.Info(
            "Initialized COGR identities for agent {0}, body {1}, entity {2}; awaiting active connection authority",
            component.AgentId,
            component.BodyId,
            uid);

        if (EntityManager.TrySystem<COGRMindOverrideSystem>(out var mindSystem))
            mindSystem.ConfigureEntityForCOGRControl(uid);

        if (_boundConnection.HasValue)
            BindBody(uid, component, _boundConnection.Value);
    }

    private void OnBodyShutdown(
        EntityUid uid,
        COGRControlledComponent component,
        ComponentShutdown args)
    {
        if (EntityManager.TrySystem<COGRBodyMotionSensationSystem>(out var bodyMotion))
            bodyMotion.NotifyControlledBodyAuthorityRemoved(uid);

        if (component.BodyId != Guid.Empty)
        {
            var existingBodyId = BodyId.FromGuid(component.BodyId);
            var existingLease = _executor.GetBodyAuthority(existingBodyId);
            if (existingLease.HasValue)
                InvalidateBodyReferences(existingLease.Value, "body_authority_revoked");
        }

        // Adapter mapping/lifecycle and action authority share this single controlled-body
        // shutdown path so no cleanup responsibility is lost when duplicate subscriptions are
        // removed from the legacy registration system.
        _adapter.UnregisterAgent(uid);

        if (component.BodyId == Guid.Empty)
            return;

        var bodyId = BodyId.FromGuid(component.BodyId);
        var results = _executor.RevokeBodyAuthorityAndFailActions(
            bodyId,
            ActionFailureReason.BodyAuthorityRevoked,
            $"Body authority ended for entity {uid}");
        EmitTerminalResults(results);

        _sawmill.Info("Rotated COGR authority for body {0} on entity shutdown {1}", bodyId, uid);
    }

    private void BindAllBodies(ConnectionId connectionId)
    {
        var count = 0;
        var query = EntityQueryEnumerator<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (BindBody(uid, component, connectionId))
                count++;
        }

        _sawmill.Info(
            "Bound {0} COGR body authorities to connection {1}",
            count,
            connectionId);
    }

    private bool BindBody(
        EntityUid uid,
        COGRControlledComponent component,
        ConnectionId connectionId)
    {
        if (component.AgentId == Guid.Empty || component.BodyId == Guid.Empty)
        {
            _sawmill.Warning(
                "Refusing to bind authority for COGR entity {0}: agent or body identity is unassigned",
                uid);
            return false;
        }

        var agentId = AgentId.FromGuid(component.AgentId);
        var bodyId = BodyId.FromGuid(component.BodyId);
        var previousLease = _executor.GetBodyAuthority(bodyId);
        if (previousLease.HasValue)
            InvalidateBodyReferences(previousLease.Value, "body_authority_rotated");

        _executor.RegisterAgentBody(agentId, bodyId, connectionId);

        var lease = _executor.GetBodyAuthority(bodyId);
        if (!lease.HasValue ||
            !lease.Value.IsValid ||
            lease.Value.AgentId != agentId ||
            lease.Value.BodyId != bodyId ||
            lease.Value.ConnectionId != connectionId ||
            lease.Value.Generation == 0)
        {
            _sawmill.Error(
                "COGR body authority did not bind to the active connection for entity {0}",
                uid);
            return false;
        }

        // Authority is the lifecycle edge that makes body-scoped cognition inputs valid.
        // Re-dirty semantic membership after the lease exists, then publish initial support and
        // establish passive body-motion continuity before later body-scoped cognitive evidence.
        if (EntityManager.TrySystem<COGRSemanticReplicaSystem>(out var semanticReplica))
            semanticReplica.NotifyControlledBodyMembershipChanged();
        if (EntityManager.TrySystem<COGREmbodimentSupportSystem>(out var embodimentSupport))
            embodimentSupport.NotifyControlledBodyAuthorityBound(uid, component);
        if (EntityManager.TrySystem<COGRBodyMotionSensationSystem>(out var bodyMotion))
            bodyMotion.NotifyControlledBodyAuthorityBound(uid, component);

        _sawmill.Debug(
            "Bound COGR authority: agent {0}, body {1}, connection {2}, generation {3}",
            agentId,
            bodyId,
            connectionId,
            lease.Value.Generation);
        return true;
    }

    private void RotateAllBodies(
        ConnectionId connectionId,
        ActionFailureReason failureReason,
        string detail)
    {
        var count = 0;
        var query = EntityQueryEnumerator<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (component.BodyId == Guid.Empty)
                continue;

            var bodyId = BodyId.FromGuid(component.BodyId);
            var lease = _executor.GetBodyAuthority(bodyId);
            if (!lease.HasValue || lease.Value.ConnectionId != connectionId)
                continue;

            if (EntityManager.TrySystem<COGRBodyMotionSensationSystem>(out var bodyMotion))
                bodyMotion.NotifyControlledBodyAuthorityRemoved(uid);
            InvalidateBodyReferences(lease.Value, "connection_authority_ended");

            var results = _executor.RevokeBodyAuthorityAndFailActions(
                bodyId,
                failureReason,
                detail);
            EmitTerminalResults(results);
            count++;
        }

        _sawmill.Info(
            "Rotated {0} COGR body authority generations for connection {1}: {2}",
            count,
            connectionId,
            detail);
    }

    private void InvalidateBodyReferences(BodyAuthorityLease lease, string reason)
    {
        if (EntityManager.TrySystem<COGRBoundedPerceptionSystem>(out var perceptionSystem))
            perceptionSystem.InvalidateBodyAuthority(lease, reason);
    }

    private void InvalidateConnectionReferences(ConnectionId connectionId, string reason)
    {
        if (EntityManager.TrySystem<COGRBoundedPerceptionSystem>(out var perceptionSystem))
            perceptionSystem.InvalidateConnection(connectionId, reason);
    }

    private void EmitTerminalResults(IEnumerable<ActionResult> results)
    {
        if (!_boundConnection.HasValue)
            return;

        foreach (var result in results)
            _bridge.OnActionTerminalResult(result);
    }
}
