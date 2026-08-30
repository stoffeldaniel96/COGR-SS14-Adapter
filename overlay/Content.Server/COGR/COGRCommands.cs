using System;
using System.Net.Http;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.COGR.Actions;
using Content.Shared.Administration;
using Content.Shared.COGR.Components;
using Robust.Shared.Console;

namespace Content.Server.COGR;

/// <summary>
/// Console command to display COGR adapter diagnostics.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRStatusCommand : LocalizedCommands
{
    [Dependency] private IEntitySystemManager _entitySystems = default!;

    public override string Command => "cogr_status";
    public override string Description => "Displays COGR adapter status and diagnostics.";
    public override string Help => "cogr_status";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!_entitySystems.TryGetEntitySystem<COGRAdapterSystem>(out var cogrSystem))
        {
            shell.WriteLine("COGR Adapter System not found.");
            return;
        }

        var diag = cogrSystem.GetDiagnostics();
        shell.WriteLine(diag.ToString());
    }
}

/// <summary>
/// Console command to connect to COGR runtime.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRConnectCommand : LocalizedCommands
{
    [Dependency] private IEntitySystemManager _entitySystems = default!;

    public override string Command => "cogr_connect";
    public override string Description => "Connects to the COGR runtime.";
    public override string Help => "cogr_connect";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!_entitySystems.TryGetEntitySystem<COGRAdapterSystem>(out var cogrSystem))
        {
            shell.WriteLine("COGR Adapter System not found.");
            return;
        }

        if (!cogrSystem.IsEnabled)
        {
            shell.WriteLine("COGR Adapter is disabled.");
            return;
        }

        if (cogrSystem.IsConnected)
        {
            shell.WriteLine("Already connected to COGR runtime.");
            return;
        }

        shell.WriteLine($"Connecting to COGR runtime at {cogrSystem.Configuration.RuntimeEndpoint}...");

        var result = cogrSystem.Connection?.ConnectAsync().GetAwaiter().GetResult() ?? false;

        if (result)
        {
            shell.WriteLine("Connected to COGR runtime.");
        }
        else
        {
            shell.WriteLine("Failed to connect to COGR runtime.");
        }
    }
}

/// <summary>
/// Console command to disconnect from COGR runtime.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRDisconnectCommand : LocalizedCommands
{
    [Dependency] private IEntitySystemManager _entitySystems = default!;

    public override string Command => "cogr_disconnect";
    public override string Description => "Disconnects from the COGR runtime.";
    public override string Help => "cogr_disconnect";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!_entitySystems.TryGetEntitySystem<COGRAdapterSystem>(out var cogrSystem))
        {
            shell.WriteLine("COGR Adapter System not found.");
            return;
        }

        if (!cogrSystem.IsConnected)
        {
            shell.WriteLine("Not connected to COGR runtime.");
            return;
        }

        shell.WriteLine("Disconnecting from COGR runtime...");
        cogrSystem.Connection?.DisconnectAsync().GetAwaiter().GetResult();
        shell.WriteLine("Disconnected from COGR runtime.");
    }
}

/// <summary>
/// Console command to list registered COGR agents.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRAgentsCommand : LocalizedCommands
{
    [Dependency] private IEntitySystemManager _entitySystems = default!;
    [Dependency] private IEntityManager _entityManager = default!;

    public override string Command => "cogr_agents";
    public override string Description => "Lists all registered COGR agents.";
    public override string Help => "cogr_agents";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!_entitySystems.TryGetEntitySystem<COGRAdapterSystem>(out var cogrSystem))
        {
            shell.WriteLine("COGR Adapter System not found.");
            return;
        }

        if (cogrSystem.EntityMapper == null)
        {
            shell.WriteLine("Entity mapper not initialized.");
            return;
        }

        var mappings = cogrSystem.EntityMapper.GetMappingSnapshot();

        if (mappings.Count == 0)
        {
            shell.WriteLine("No agents registered.");
            return;
        }

        shell.WriteLine($"Registered agents ({mappings.Count}):");
        foreach (var (entityUid, agentId) in mappings)
        {
            var name = _entityManager.TryGetComponent<MetaDataComponent>(entityUid, out var meta)
                ? meta.EntityName
                : "Unknown";
            shell.WriteLine($"  {entityUid} ({name}) -> {agentId}");
        }
    }
}

/// <summary>
/// Console command to register a test entity as a COGR agent.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRRegisterTestCommand : LocalizedCommands
{
    [Dependency] private IEntitySystemManager _entitySystems = default!;
    [Dependency] private IEntityManager _entityManager = default!;

    public override string Command => "cogr_register_test";
    public override string Description => "Registers a test entity as a COGR agent.";
    public override string Help => "cogr_register_test <entityUid>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!_entitySystems.TryGetEntitySystem<COGRAdapterSystem>(out var cogrSystem))
        {
            shell.WriteLine("COGR Adapter System not found.");
            return;
        }

        if (args.Length < 1)
        {
            shell.WriteLine("Usage: cogr_register_test <entityUid>");
            return;
        }

        if (!EntityUid.TryParse(args[0], out var entityUid))
        {
            shell.WriteLine($"Invalid entity UID: {args[0]}");
            return;
        }

        if (!_entityManager.EntityExists(entityUid))
        {
            shell.WriteLine($"Entity {entityUid} does not exist.");
            return;
        }

        var agentId = cogrSystem.RegisterAgent(entityUid);
        if (agentId != null)
        {
            shell.WriteLine($"Registered entity {entityUid} as agent {agentId}");
        }
        else
        {
            shell.WriteLine($"Failed to register entity {entityUid}");
        }
    }
}

/// <summary>
/// Console command to ping the COGR runtime and verify bidirectional communication.
/// F0.5: Proves that SS14 can send a request and receive a response from COGR.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRPingCommand : LocalizedCommands
{
    [Dependency] private IEntitySystemManager _entitySystems = default!;

    public override string Command => "cogr_ping";
    public override string Description => "Pings the COGR runtime to verify bidirectional communication.";
    public override string Help => "cogr_ping";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!_entitySystems.TryGetEntitySystem<COGRAdapterSystem>(out var cogrSystem))
        {
            shell.WriteLine("COGR Adapter System not found.");
            return;
        }

        if (!cogrSystem.IsEnabled)
        {
            shell.WriteLine("COGR Adapter is disabled.");
            return;
        }

        var endpoint = cogrSystem.Configuration.RuntimeEndpoint;
        shell.WriteLine($"Pinging COGR runtime at {endpoint}...");

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            var response = httpClient.GetAsync($"{endpoint}/api/health").GetAwaiter().GetResult();
            
            if (response.IsSuccessStatusCode)
            {
                var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                shell.WriteLine($"✓ COGR Runtime responded: {response.StatusCode}");
                shell.WriteLine($"  Response: {content}");
                shell.WriteLine("  Bidirectional communication verified!");
            }
            else
            {
                shell.WriteLine($"✗ COGR Runtime returned error: {response.StatusCode}");
            }
        }
        catch (HttpRequestException ex)
        {
            shell.WriteLine($"✗ Failed to ping COGR runtime: {ex.Message}");
            shell.WriteLine("  Is the COGR runtime running?");
        }
        catch (TaskCanceledException)
        {
            shell.WriteLine("✗ Ping timeout - COGR runtime did not respond within 5 seconds");
        }
    }
}

/// <summary>
/// Console command to test COGR action execution by making an entity face a direction.
/// F1: Verifies the embodiment path with a non-cognitive action.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRTestFaceCommand : LocalizedCommands
{
    [Dependency] private IEntitySystemManager _entitySystems = default!;
    [Dependency] private IEntityManager _entityManager = default!;

    public override string Command => "cogr_test_face";
    public override string Description => "Makes a COGR-controlled entity face a direction.";
    public override string Help => "cogr_test_face <entityUid|agentId> <direction>";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteLine("Usage: cogr_test_face <entityUid|agentId> <direction>");
            shell.WriteLine("  direction: north, south, east, west, or degrees (0-360)");
            return;
        }

        // Find the entity
        EntityUid? entityUid = null;

        if (EntityUid.TryParse(args[0], out var uid))
        {
            entityUid = uid;
        }
        else if (Guid.TryParse(args[0], out var agentId))
        {
            // Look up by agent ID
            if (_entitySystems.TryGetEntitySystem<COGRAdapterSystem>(out var cogrSystem) &&
                cogrSystem.EntityMapper != null)
            {
                entityUid = cogrSystem.EntityMapper.GetEntityUid(agentId);
            }
        }

        if (entityUid == null || !_entityManager.EntityExists(entityUid.Value))
        {
            shell.WriteLine($"Entity not found: {args[0]}");
            return;
        }

        if (!_entityManager.HasComponent<COGRControlledComponent>(entityUid.Value))
        {
            shell.WriteLine($"Entity {entityUid} is not COGR-controlled");
            return;
        }

        // Get the action executor
        if (!_entitySystems.TryGetEntitySystem<COGRActionExecutor>(out var actionExecutor))
        {
            shell.WriteLine("COGR Action Executor not found.");
            return;
        }

        // Execute the face direction action
        var parameters = new Dictionary<string, object>
        {
            ["direction"] = args[1]
        };

        // Legacy F1 command - F02 uses different action system
        shell.WriteLine("This command is deprecated. Use 'cogr_propose_action' for F02 action system.");
        shell.WriteLine($"Example: cogr_propose_action {entityUid} movement.turn {{\"direction\":\"{args[1]}\"}}");
        return;
        
        // var result = actionExecutor.ExecuteAction(entityUid.Value, "movement.face_direction", parameters);
    }
}

/// <summary>
/// Console command to test COGR action execution by making an entity take a step.
/// F1: Verifies the embodiment path with a non-cognitive action.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRTestStepCommand : LocalizedCommands
{
    [Dependency] private IEntitySystemManager _entitySystems = default!;
    [Dependency] private IEntityManager _entityManager = default!;

    public override string Command => "cogr_test_step";
    public override string Description => "Makes a COGR-controlled entity take one step in a direction.";
    public override string Help => "cogr_test_step <entityUid|agentId> [direction]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteLine("Usage: cogr_test_step <entityUid|agentId> [direction]");
            shell.WriteLine("  direction: north, south, east, west, or degrees (0-360)");
            shell.WriteLine("  If no direction is given, steps in current facing direction.");
            return;
        }

        // Find the entity
        EntityUid? entityUid = null;

        if (EntityUid.TryParse(args[0], out var uid))
        {
            entityUid = uid;
        }
        else if (Guid.TryParse(args[0], out var agentId))
        {
            // Look up by agent ID
            if (_entitySystems.TryGetEntitySystem<COGRAdapterSystem>(out var cogrSystem) &&
                cogrSystem.EntityMapper != null)
            {
                entityUid = cogrSystem.EntityMapper.GetEntityUid(agentId);
            }
        }

        if (entityUid == null || !_entityManager.EntityExists(entityUid.Value))
        {
            shell.WriteLine($"Entity not found: {args[0]}");
            return;
        }

        if (!_entityManager.HasComponent<COGRControlledComponent>(entityUid.Value))
        {
            shell.WriteLine($"Entity {entityUid} is not COGR-controlled");
            return;
        }

        // Get the action executor
        if (!_entitySystems.TryGetEntitySystem<COGRActionExecutor>(out var actionExecutor))
        {
            shell.WriteLine("COGR Action Executor not found.");
            return;
        }

        // Execute the step action
        var parameters = new Dictionary<string, object>();
        if (args.Length >= 2)
        {
            parameters["direction"] = args[1];
        }

        // Legacy F1 command - F02 uses different action system
        var direction = args.Length >= 2 ? args[1] : "north";
        shell.WriteLine("This command is deprecated. Use 'cogr_propose_action' for F02 action system.");
        shell.WriteLine($"Example: cogr_propose_action {entityUid} movement.step {{\"direction\":\"{direction}\"}}");
        return;
    }
}

/// <summary>
/// Console command to list COGR-controlled entities specifically.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRControlledCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;

    public override string Command => "cogr_controlled";
    public override string Description => "Lists all COGR-controlled entities (spawned humanoids).";
    public override string Help => "cogr_controlled";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var query = _entityManager.EntityQueryEnumerator<COGRControlledComponent, TransformComponent>();
        var count = 0;

        shell.WriteLine("COGR Controlled Entities:");
        shell.WriteLine("========================");

        while (query.MoveNext(out var uid, out var cogr, out var xform))
        {
            var name = _entityManager.TryGetComponent<MetaDataComponent>(uid, out var meta)
                ? meta.EntityName
                : "Unknown";

            var pos = xform.LocalPosition;
            var activeStr = cogr.IsActive ? "Active" : "Inactive";

            shell.WriteLine($"  Entity {uid} | {name}");
            shell.WriteLine($"    AgentId: {cogr.AgentId}");
            shell.WriteLine($"    BodyId:  {cogr.BodyId}");
            shell.WriteLine($"    Status: {activeStr}");
            shell.WriteLine($"    Position: ({pos.X:F2}, {pos.Y:F2})");
            shell.WriteLine($"    Last Action Tick: {cogr.LastActionTick}");
            shell.WriteLine("---");
            count++;
        }

        shell.WriteLine($"Total: {count} controlled entity(ies)");
    }
}
