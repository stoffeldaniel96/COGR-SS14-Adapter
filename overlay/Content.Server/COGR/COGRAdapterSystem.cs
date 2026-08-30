using System;
using System.Threading.Tasks;
using COGR.Core.Actions;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Time;
using COGR.SS14Bridge;
using Content.Server.COGR.Actions;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Server.COGR;

/// <summary>
/// Main adapter system for COGR cognitive agent integration.
/// This system runs inside SS14 and manages the connection to the external COGR runtime.
/// </summary>
/// <remarks>
/// F0.5 Scope:
/// - Loads with the server
/// - Connects to COGR runtime
/// - Translates SS14 lifecycle events to COGR messages
/// - Maps SS14 entities to COGR AgentIds
/// - Provides diagnostics and logging
///
/// Out of scope for F0.5:
/// - Perception, planning, memory, actions (F1+)
/// </remarks>
public sealed partial class COGRAdapterSystem : EntitySystem
{
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private IGameTiming _gameTiming = default!;

    private ISawmill _sawmill = default!;
    private bool _wasConnected;
    private COGRActionExecutor? _actionExecutor;
    private ActionBridge? _actionBridge;
    private COGRReferenceRegistry? _referenceRegistry;

    /// <summary>
    /// Gets the adapter configuration.
    /// </summary>
    public COGRAdapterConfiguration Configuration { get; private set; } = new();

    /// <summary>
    /// Gets the connection manager.
    /// </summary>
    public COGRConnectionManager? Connection { get; private set; }

    /// <summary>
    /// Gets the entity mapper for SS14 UID to COGR AgentId mapping.
    /// </summary>
    public COGREntityMapper? EntityMapper { get; private set; }

    /// <summary>
    /// Gets the reference registry for opaque environment references.
    /// </summary>
    public COGRReferenceRegistry? ReferenceRegistry => _referenceRegistry;

    /// <summary>
    /// Gets whether the adapter is enabled.
    /// </summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Gets whether the adapter is connected to COGR runtime.
    /// </summary>
    public bool IsConnected => Connection?.IsConnected ?? false;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("cogr.adapter");
        _sawmill.Info("COGR Adapter System initializing...");

        // Load configuration from CVars
        LoadConfiguration();

        if (!IsEnabled)
        {
            _sawmill.Info("COGR Adapter is disabled (cogr.enabled = false)");
            return;
        }

        // Initialize subsystems
        InitializeConnection();
        InitializeEntityMapper();
        InitializeLifecycleHandlers();
        InitializeActionSystem();
        InitializePerceptionRouting();

        _sawmill.Info("COGR Adapter System initialized");
        
        // Attempt immediate connection
        if (Configuration.AutoConnect && Connection != null)
        {
            _sawmill.Info("Attempting immediate connection to COGR runtime...");
            _ = ConnectWithLogging();
        }
    }

    public override void Shutdown()
    {
        base.Shutdown();

        if (!IsEnabled)
            return;

        _sawmill.Info("COGR Adapter System shutting down...");

        // Disconnect and cleanup
        ShutdownPerceptionRouting();
        Connection?.DisconnectAsync().GetAwaiter().GetResult();
        Connection?.Dispose();
        EntityMapper?.Clear();

        _sawmill.Info("COGR Adapter System shutdown complete");
    }

    private static bool _firstUpdateLogged = false;
    
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_firstUpdateLogged)
        {
            _sawmill.Info("COGR Update() called for the first time!");
            _firstUpdateLogged = true;
        }

        if (!IsEnabled || Connection == null)
            return;

        // Check if we just connected and need to resync existing agents
        if (Connection.IsConnected && !_wasConnected)
        {
            _sawmill.Info("Connection established - resyncing {0} existing agents", EntityMapper?.Count ?? 0);
            ResyncExistingAgents();
            OnConnectionStateChanged(true);
            _wasConnected = true;
        }
        else if (!Connection.IsConnected && _wasConnected)
        {
            OnConnectionStateChanged(false);
            _wasConnected = false;
        }

        // Process pending messages and let the connection manager own reconnect timing/backoff.
        Connection.ProcessPendingMessages();

        // F02: Tick action executor and send results
        if (_actionExecutor != null)
        {
            var currentTick = (ulong)_gameTiming.CurTick.Value;
            var results = _actionExecutor.TickActions(currentTick);

            // Send terminal results back to runtime via bridge
            if (_actionBridge != null && results.Count > 0)
            {
                foreach (var result in results)
                {
                    _actionBridge.OnActionTerminalResult(result);
                }
            }
        }

        // Update heartbeat/liveness
        Connection.UpdateHeartbeat(_gameTiming.CurTick.Value);
    }

    private void LoadConfiguration()
    {
        // Check if COGR is enabled via CVar
        // For F0.5, we default to enabled if the CVar doesn't exist
        IsEnabled = true; // TODO: Read from cogr.enabled CVar when registered

        Configuration = new COGRAdapterConfiguration
        {
            RuntimeEndpoint = "http://localhost:5050", // TODO: Read from cogr.runtime_endpoint CVar
            LaunchToken = "dev-token-f05",             // TODO: Read from cogr.launch_token CVar
            AutoConnect = true,                         // TODO: Read from cogr.auto_connect CVar
            HeartbeatIntervalTicks = 30,               // 1 second at 30 TPS
            ReconnectDelayMs = 5000,
        };

        _sawmill.Debug("COGR Configuration loaded - Endpoint: {0}, AutoConnect: {1}",
            Configuration.RuntimeEndpoint,
            Configuration.AutoConnect);
    }

    private void InitializeConnection()
    {
        var connectionSawmill = _logManager.GetSawmill("cogr.connection");
        Connection = new COGRConnectionManager(Configuration, connectionSawmill);

        if (Configuration.AutoConnect)
        {
            _sawmill.Info("Auto-connecting to COGR runtime at {0}...", Configuration.RuntimeEndpoint);
            // Initialize() performs the first attempt; the connection manager owns later retries.
        }
    }

    private void InitializeEntityMapper()
    {
        EntityMapper = new COGREntityMapper(_sawmill);
        _referenceRegistry = new COGRReferenceRegistry(_sawmill, EntityManager);
    }

    private void InitializeLifecycleHandlers()
    {
        // Subscribe to SS14 round lifecycle events
        SubscribeLocalEvent<RoundStartingEvent>(OnRoundStarting);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameRunLevelChanged);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundCleanup);

        _sawmill.Debug("Lifecycle event handlers registered");
    }

    private void OnRoundStarting(RoundStartingEvent ev)
    {
        _sawmill.Info("Round {0} starting - notifying COGR runtime", ev.Id);

        if (Connection == null || !Connection.IsConnected)
        {
            _sawmill.Warning("Cannot notify COGR: not connected");
            return;
        }

        // Send WorldLifecycleMessage (Started)
        Connection.SendWorldLifecycle(WorldLifecycleType.Started, $"Round {ev.Id}");
    }

    private void OnGameRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        _sawmill.Debug("Game run level changed: {0} -> {1}", ev.Old, ev.New);

        if (Connection == null || !Connection.IsConnected)
            return;

        // Map SS14 run levels to COGR lifecycle
        if (ev.New == GameRunLevel.PostRound)
        {
            _sawmill.Info("Round ended - notifying COGR runtime");
            Connection.SendWorldLifecycle(WorldLifecycleType.Stopping, "Round ended");
            EntityMapper?.Clear();
        }
        else if (ev.New == GameRunLevel.PreRoundLobby && ev.Old == GameRunLevel.PostRound)
        {
            _sawmill.Debug("Returning to lobby - round cleanup");
            EntityMapper?.Clear();
        }
    }

    private void OnRoundCleanup(RoundRestartCleanupEvent ev)
    {
        _sawmill.Debug("Round cleanup - clearing COGR state");
        EntityMapper?.Clear();
    }

    /// <summary>
    /// Registers an SS14 entity as a COGR agent.
    /// Uses the AgentId from COGRControlledComponent if present, otherwise generates a new one.
    /// </summary>
    /// <param name="entityUid">The SS14 entity UID.</param>
    /// <returns>The assigned COGR AgentId, or null if registration failed.</returns>
    public Guid? RegisterAgent(EntityUid entityUid)
    {
        if (EntityMapper == null)
        {
            _sawmill.Warning("Cannot register agent: EntityMapper not initialized");
            return null;
        }

        // Check if entity has COGRControlledComponent with an AgentId
        Guid? existingAgentId = null;
        if (TryComp<Content.Shared.COGR.Components.COGRControlledComponent>(entityUid, out var controlled))
        {
            if (controlled.AgentId != Guid.Empty)
            {
                existingAgentId = controlled.AgentId;
            }
        }

        var agentId = EntityMapper.RegisterEntity(entityUid, existingAgentId);

        if (Connection?.IsConnected == true)
        {
            Connection.SendAgentLifecycle(agentId, AgentLifecycleType.Spawned, $"Entity {entityUid}");
        }

        _sawmill.Info("Registered SS14 entity {0} as COGR agent {1}", entityUid, agentId);
        return agentId;
    }

    /// <summary>
    /// Unregisters an SS14 entity from COGR.
    /// </summary>
    /// <param name="entityUid">The SS14 entity UID.</param>
    public void UnregisterAgent(EntityUid entityUid)
    {
        if (EntityMapper == null)
            return;

        var agentId = EntityMapper.GetAgentId(entityUid);
        if (agentId == null)
            return;

        if (Connection?.IsConnected == true)
        {
            Connection.SendAgentLifecycle(agentId.Value, AgentLifecycleType.Despawned, $"Entity {entityUid}");
        }

        EntityMapper.UnregisterEntity(entityUid);
        _sawmill.Info("Unregistered SS14 entity {0} (agent {1})", entityUid, agentId);
    }

    /// <summary>
    /// Wrapper for ConnectAsync that logs exceptions instead of swallowing them.
    /// </summary>
    private async Task ConnectWithLogging()
    {
        try
        {
            var result = await Connection!.ConnectAsync();
            
            if (result)
            {
                _sawmill.Info("Connected to COGR runtime");
            }
        }
        catch (Exception ex)
        {
            _sawmill.Error("Exception during connection attempt: {0}", ex.Message);
        }
    }

    /// <summary>
    /// Called when the connection state changes.
    /// Updates all COGR-controlled entities with the new state.
    /// </summary>
    private void OnConnectionStateChanged(bool connected)
    {
        _sawmill.Info("COGR connection state changed: {0}", connected ? "Connected" : "Disconnected");

        // Update all COGR-controlled entities
        if (EntityManager.TrySystem<Systems.COGRExamineSystem>(out var examineSystem))
        {
            examineSystem.UpdateAllEntitiesConnectionState(connected);
        }
    }

    /// <summary>
    /// Resyncs all existing agents with the COGR runtime after connection is established.
    /// This handles the case where entities spawned before the connection was ready.
    /// </summary>
    private void ResyncExistingAgents()
    {
        if (EntityMapper == null || Connection == null || !Connection.IsConnected)
            return;

        // Send agent lifecycle events for all registered entities
        var entities = EntityMapper.GetAllEntityUids();
        foreach (var entityUid in entities)
        {
            var agentId = EntityMapper.GetAgentId(entityUid);
            if (agentId != null)
            {
                Connection.SendAgentLifecycle(agentId.Value, AgentLifecycleType.Spawned, $"Entity {entityUid} (resynced)");
                _sawmill.Debug("Resynced agent {0} for entity {1}", agentId, entityUid);
            }
        }
    }

    /// <summary>
    /// Initializes the F02 action system and wires up bridge callbacks.
    /// </summary>
    private void InitializeActionSystem()
    {
        _sawmill.Info("Initializing F02/F03 action system...");
        
        // Create reference registry for opaque environment references
        var refSawmill = _logManager.GetSawmill("cogr.references");
        _referenceRegistry = new COGRReferenceRegistry(refSawmill, EntityManager);
        
        // Get the action executor system
        _actionExecutor = EntityManager.System<COGRActionExecutor>();
        
        // Wire up strict action-context-aware reference resolution for interaction actions.
        _actionExecutor.SetReferenceResolver(ResolveActionTarget);
        
        // TODO: Wire up action bridge when SS14Bridge is integrated
        // For now, action system is initialized and can be used via commands
        
        _sawmill.Info("F02/F03 action system initialized with strict reference resolution");
    }

    /// <summary>
    /// Resolves an action target only under the current active connection and exact current
    /// agent/body authority lease. Any stale, cross-connection, cross-body, or rotated-generation
    /// reference fails closed before a native SS14 interaction is attempted.
    /// </summary>
    private EntityUid? ResolveActionTarget(ActionAttempt attempt, EnvironmentRef environmentReference)
    {
        if (_referenceRegistry == null ||
            _actionExecutor == null ||
            Connection is not { IsConnected: true } connection ||
            connection.ConnectionId == Guid.Empty)
        {
            return null;
        }

        var activeConnectionId = ConnectionId.FromGuid(connection.ConnectionId);
        var lease = attempt.AuthorityLease;
        if (!lease.IsValid ||
            lease.AgentId != attempt.AgentId ||
            lease.BodyId != attempt.BodyId ||
            lease.ConnectionId != activeConnectionId)
        {
            return null;
        }

        var currentLease = _actionExecutor.GetBodyAuthority(attempt.BodyId);
        if (!currentLease.HasValue || !lease.Matches(currentLease.Value))
            return null;

        return _referenceRegistry.TryResolve(
            environmentReference,
            new EnvironmentReferenceResolutionContext
            {
                ConnectionId = activeConnectionId,
                CurrentTick = new SimTick((ulong)_gameTiming.CurTick.Value),
                BodyId = attempt.BodyId,
                BodyGeneration = lease.Generation,
            });
    }

    /// <summary>
    /// Called when an action proposal is received from the runtime.
    /// This will be called by the bridge once it's fully integrated.
    /// </summary>
    public void OnActionProposalReceived(ActionAttempt attempt)
    {
        if (_actionExecutor == null)
        {
            _sawmill.Error("Action executor not initialized");
            return;
        }

        // Propose the action
        var proposalResult = _actionExecutor.ProposeAction(attempt);

        // Send disposition back to runtime via bridge
        if (_actionBridge != null)
        {
            _actionBridge.OnActionDisposition(
                attempt.ProposalId,
                proposalResult.IsAccepted,
                proposalResult.RejectionReason,
                proposalResult.Detail);
        }

        // If accepted, start execution
        if (proposalResult.IsAccepted)
        {
            var execResult = _actionExecutor.StartAction(attempt.ProposalId);

            // Handle execution result
            if (!execResult.IsSuccess)
            {
                // Report failure
                var failureReason = execResult.FailureReason ?? ActionFailureReason.Unspecified;
                var result = ActionResult.Failed(
                    attempt.ProposalId,
                    new global::COGR.Core.Time.SimTick((ulong)_gameTiming.CurTick.Value),
                    failureReason,
                    execResult.Detail);

                if (_actionBridge != null)
                {
                    _actionBridge.OnActionTerminalResult(result);
                }
            }
            else if (!execResult.IsStarted)
            {
                // Every immediate successful execution is terminal, whether or not the capability has optional result data.
                // Dropping null-data completions leaves Runtime Intents permanently in flight after Station has already removed
                // the action from its registry.
                var result = ActionResult.Completed(
                    attempt.ProposalId,
                    new global::COGR.Core.Time.SimTick((ulong)_gameTiming.CurTick.Value),
                    execResult.ResultData,
                    execResult.Detail);

                if (_actionBridge != null)
                {
                    _actionBridge.OnActionTerminalResult(result);
                }
            }
            // If IsStarted, results will come from TickActions()
        }
    }

    /// <summary>
    /// Gets diagnostic information about the adapter state.
    /// </summary>
    public COGRDiagnostics GetDiagnostics()
    {
        return new COGRDiagnostics
        {
            IsEnabled = IsEnabled,
            IsConnected = IsConnected,
            RuntimeEndpoint = Configuration.RuntimeEndpoint,
            ProtocolVersion = $"{COGRConnectionManager.ProtocolMajorVersion}.{COGRConnectionManager.ProtocolMinorVersion}",
            WorldId = Connection?.WorldId,
            ConnectionId = Connection?.ConnectionId,
            RegisteredAgentCount = EntityMapper?.Count ?? 0,
            CurrentTick = _gameTiming.CurTick.Value,
            LastHeartbeatTick = Connection?.LastHeartbeatTick ?? 0,
            ConnectionState = Connection?.State.ToString() ?? "None",
            MessagesSent = Connection?.MessagesSent ?? 0,
            MessagesReceived = Connection?.MessagesReceived ?? 0,
            LastError = Connection?.LastError,
        };
    }
}

/// <summary>
/// World lifecycle event types (mirrors COGR contract).
/// </summary>
public enum WorldLifecycleType
{
    Unspecified = 0,
    Created = 1,
    Started = 2,
    Paused = 3,
    Resumed = 4,
    Stopping = 5,
    Destroyed = 6,
}

/// <summary>
/// Agent lifecycle event types (mirrors COGR contract).
/// </summary>
public enum AgentLifecycleType
{
    Unspecified = 0,
    Spawned = 1,
    Activated = 2,
    Deactivated = 3,
    Despawned = 4,
}
