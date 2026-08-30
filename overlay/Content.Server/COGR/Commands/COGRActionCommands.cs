using System.Linq;
using System.Text.Json;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using Content.Server.Administration;
using Content.Server.COGR.Actions;
using Content.Shared.Administration;
using Content.Shared.COGR.Components;
using Robust.Shared.Console;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Commands;

/// <summary>
/// Admin commands for manually testing F02 action lifecycle.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRProposeActionCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override string Command => "cogr_propose_action";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3)
        {
            shell.WriteError("Usage: cogr_propose_action <entityUid> <capability> <params-json>");
            shell.WriteError("Example: cogr_propose_action 42 movement.turn {\"direction\":\"north\"}");
            return;
        }

        if (!EntityUid.TryParse(args[0], out var entityUid))
        {
            shell.WriteError($"Invalid entity UID: {args[0]}");
            return;
        }

        if (!_entityManager.TryGetComponent<COGRControlledComponent>(entityUid, out var controlled))
        {
            shell.WriteError($"Entity {entityUid} is not COGR-controlled");
            return;
        }

        // Validate that the component has valid IDs
        if (controlled.AgentId == Guid.Empty || controlled.BodyId == Guid.Empty)
        {
            shell.WriteError($"Entity {entityUid} has COGRControlledComponent but AgentId or BodyId is not set.");
            shell.WriteError($"AgentId: {controlled.AgentId}, BodyId: {controlled.BodyId}");
            shell.WriteError($"The entity needs to be properly registered with COGR. Try spawning a new entity or manually setting the IDs.");
            return;
        }

        var capability = args[1];
        
        // Extract JSON from the raw command string to preserve quotes
        // argStr contains the full command line after the command name
        // Format in argStr: "<entityUid> <capability> <json>"
        // We have entityUid in args[0] and capability in args[1]
        // Find where capability ends in argStr and extract everything after
        var paramsJson = "{}";
        
        // Find entityUid in argStr
        var entityUidStr = args[0];
        var entityUidIndex = argStr.IndexOf(entityUidStr);
        if (entityUidIndex >= 0)
        {
            // Find the start of capability after entityUid
            var afterEntityUid = entityUidIndex + entityUidStr.Length;
            var capabilityIndex = argStr.IndexOf(capability, afterEntityUid);
            
            if (capabilityIndex >= 0)
            {
                // Everything after capability is the JSON
                var afterCapability = capabilityIndex + capability.Length;
                if (afterCapability < argStr.Length)
                {
                    paramsJson = argStr.Substring(afterCapability).TrimStart();
                }
            }
        }

        // Parse capability
        var actionCapability = ParseCapability(capability);
        if (actionCapability == ActionCapability.Unknown)
        {
            shell.WriteError($"Unknown capability: {capability}");
            return;
        }

        // Serialize parameters
        var parameters = SerializeParameters(actionCapability, paramsJson, out var serializeError);
        if (parameters == null)
        {
            shell.WriteError($"Failed to serialize parameters: {serializeError ?? "Unknown error"}");
            return;
        }

        // Get action executor
        var executor = _entityManager.System<COGRActionExecutor>();

        // Create ActionAttempt
        // AgentId and BodyId in the component are already GUIDs, use them directly
        var agentId = AgentId.FromGuid(controlled.AgentId);
        var bodyId = BodyId.FromGuid(controlled.BodyId);
        var currentTick = new SimTick((ulong)_timing.CurTick.Value);

        // Get or create body authority
        var authority = executor.GetBodyAuthority(bodyId);
        if (authority == null)
        {
            // Register body authority for this entity
            var connectionId = ConnectionId.FromGuid(Guid.Empty);
            executor.RegisterAgentBody(agentId, bodyId, connectionId);
            authority = executor.GetBodyAuthority(bodyId);
            
            if (authority == null)
            {
                shell.WriteError("Failed to create body authority");
                return;
            }
        }

        var attempt = new ActionAttempt
        {
            ProposalId = ActionProposalId.NewId(),
            AgentId = agentId,
            BodyId = bodyId,
            AuthorityLease = authority.Value,
            CausalTraceId = CausalTraceId.NewId(),
            ProposedAtTick = currentTick,
            RuntimeSequence = new RuntimeSequence(1),
            Capability = actionCapability,
            Parameters = parameters.Value,
            ParameterFormat = "json",
            TimeoutMs = 0
        };

        // Propose action
        var proposalResult = executor.ProposeAction(attempt);

        shell.WriteLine($"Action proposed: ProposalId={attempt.ProposalId}");
        shell.WriteLine($"Disposition: {(proposalResult.IsAccepted ? "Accepted" : "Rejected")}");
        if (!proposalResult.IsAccepted)
        {
            shell.WriteLine($"Reason: {proposalResult.RejectionReason} - {proposalResult.Detail}");
            return;
        }

        // Start execution
        var execResult = executor.StartAction(attempt.ProposalId);
        if (!execResult.IsSuccess)
        {
            shell.WriteLine($"Execution failed: {execResult.Detail}");
            return;
        }

        if (execResult.IsStarted)
        {
            shell.WriteLine("Action started (async movement)");
        }
        else
        {
            shell.WriteLine($"Action completed: {execResult.Detail}");
        }
    }

    private static ActionCapability ParseCapability(string capability)
    {
        return capability switch
        {
            "movement.turn" => ActionCapability.MovementTurn,
            "movement.step" => ActionCapability.MovementStep,
            "movement.stop" => ActionCapability.MovementStop,
            "movement.move_to_location" => ActionCapability.MovementMoveToLocation,
            "action.cancel" => ActionCapability.ActionCancel,
            // Interaction capabilities
            "interaction.open" => ActionCapability.InteractionOpen,
            "interaction.close" => ActionCapability.InteractionClose,
            "manipulation.pickup" => ActionCapability.ManipulationPickUp,
            "manipulation.drop" => ActionCapability.ManipulationDrop,
            "manipulation.place_near" => ActionCapability.ManipulationPlaceNear,
            _ => ActionCapability.Unknown
        };
    }

    private ReadOnlyMemory<byte>? SerializeParameters(ActionCapability capability, string json, out string? error)
    {
        error = null;
        try
        {
            return capability switch
            {
                ActionCapability.MovementTurn => SerializeTurnParams(json),
                ActionCapability.MovementStep => SerializeStepParams(json),
                ActionCapability.MovementStop => SerializeStopParams(),
                ActionCapability.MovementMoveToLocation => SerializeMoveToParams(json),
                ActionCapability.ActionCancel => SerializeCancelParams(json),
                // Interaction capabilities - require reference registry
                ActionCapability.InteractionOpen => SerializeOpenParams(json),
                ActionCapability.InteractionClose => SerializeCloseParams(json),
                ActionCapability.ManipulationPickUp => SerializePickUpParams(json),
                ActionCapability.ManipulationDrop => SerializeDropParams(json),
                ActionCapability.ManipulationPlaceNear => SerializePlaceNearParams(json),
                _ => null
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static ReadOnlyMemory<byte> SerializeTurnParams(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var direction = dict!["direction"].GetString()!;
        var directionEnum = Enum.Parse<global::COGR.Core.Actions.Parameters.Direction>(direction, true);
        var p = new TurnActionParams { TargetDirection = directionEnum };
        return ActionParameterSerializer.Serialize(p);
    }

    private static ReadOnlyMemory<byte> SerializeStepParams(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var direction = dict!["direction"].GetString()!;
        var directionEnum = Enum.Parse<global::COGR.Core.Actions.Parameters.Direction>(direction, true);
        var p = new StepActionParams { Direction = directionEnum };
        return ActionParameterSerializer.Serialize(p);
    }

    private static ReadOnlyMemory<byte> SerializeStopParams()
    {
        var p = new StopActionParams();
        return ActionParameterSerializer.Serialize(p);
    }

    private static ReadOnlyMemory<byte> SerializeMoveToParams(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        
        // Support both flat format (targetX, targetY) and nested format (targetLocation.x, targetLocation.y)
        double targetX, targetY;
        if (dict!.TryGetValue("targetLocation", out var locationObj))
        {
            // Nested format: {"targetLocation":{"x":10,"y":20}}
            var location = locationObj.Deserialize<Dictionary<string, JsonElement>>();
            targetX = location!["x"].GetDouble();
            targetY = location["y"].GetDouble();
        }
        else
        {
            // Flat format (backward compatibility): {"targetX":10,"targetY":20}
            targetX = dict["targetX"].GetDouble();
            targetY = dict["targetY"].GetDouble();
        }
        
        var run = dict.TryGetValue("run", out var r) && r.GetBoolean();
        var tolerance = dict.TryGetValue("arrivalTolerance", out var t) ? t.GetDouble() : 0.5;

        var p = new MoveToLocationParams
        {
            TargetLocation = WorldLocation.Create(targetX, targetY),
            Run = run,
            ArrivalTolerance = tolerance
        };
        return ActionParameterSerializer.Serialize(p);
    }

    private static ReadOnlyMemory<byte> SerializeCancelParams(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var targetId = dict!["targetProposalId"].GetString()!;
        var p = new CancelActionParams { TargetProposalId = ActionProposalId.Parse(targetId) };
        return ActionParameterSerializer.Serialize(p);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Interaction parameter serializers
    // These accept EntityUid in JSON and convert to EnvironmentRef via registry
    // ═══════════════════════════════════════════════════════════════════════════

    private EnvironmentRef ResolveTargetToRef(string json, string fieldName = "target")
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var targetUidStr = dict![fieldName].GetInt32();
        var targetUid = new EntityUid(targetUidStr);

        var adapterSystem = _entityManager.System<COGRAdapterSystem>();
        if (adapterSystem.ReferenceRegistry == null)
        {
            throw new InvalidOperationException("Reference registry not initialized");
        }

        return adapterSystem.ReferenceRegistry.IssueSimpleReference(targetUid);
    }

    private ReadOnlyMemory<byte> SerializeOpenParams(string json)
    {
        var targetRef = ResolveTargetToRef(json);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var force = dict!.TryGetValue("force", out var f) && f.GetBoolean();

        var p = new OpenActionParams { TargetRef = targetRef, Force = force };
        return ActionParameterSerializer.Serialize(p);
    }

    private ReadOnlyMemory<byte> SerializeCloseParams(string json)
    {
        var targetRef = ResolveTargetToRef(json);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var force = dict!.TryGetValue("force", out var f) && f.GetBoolean();

        var p = new CloseActionParams { TargetRef = targetRef, Force = force };
        return ActionParameterSerializer.Serialize(p);
    }

    private ReadOnlyMemory<byte> SerializePickUpParams(string json)
    {
        var targetRef = ResolveTargetToRef(json);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var preferredHand = dict!.TryGetValue("preferredHand", out var h) ? h.GetString() : null;

        var p = new PickUpActionParams { TargetRef = targetRef, PreferredHand = preferredHand };
        return ActionParameterSerializer.Serialize(p);
    }

    private ReadOnlyMemory<byte> SerializeDropParams(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var handId = dict!.TryGetValue("handId", out var h) ? h.GetString() : null;

        var p = new DropActionParams { HandId = handId };
        return ActionParameterSerializer.Serialize(p);
    }

    private ReadOnlyMemory<byte> SerializePlaceNearParams(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var handId = dict!.TryGetValue("handId", out var h) ? h.GetString() : null;

        // PlaceNear can use either target reference or target location
        EnvironmentRef? targetRef = null;
        WorldLocation? targetLocation = null;

        if (dict.TryGetValue("target", out var t))
        {
            var targetUid = new EntityUid(t.GetInt32());
            var adapterSystem = _entityManager.System<COGRAdapterSystem>();
            if (adapterSystem.ReferenceRegistry != null)
            {
                targetRef = adapterSystem.ReferenceRegistry.IssueSimpleReference(targetUid);
            }
        }
        else if (dict.TryGetValue("x", out var x) && dict.TryGetValue("y", out var y))
        {
            targetLocation = WorldLocation.Create(x.GetDouble(), y.GetDouble());
        }

        var p = new PlaceNearActionParams
        {
            TargetRef = targetRef,
            TargetLocation = targetLocation,
            HandId = handId
        };
        return ActionParameterSerializer.Serialize(p);
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRActiveActionsCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;

    public override string Command => "cogr_active_actions";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var executor = _entityManager.System<COGRActionExecutor>();
        var registry = executor.ActionRegistry;

        if (args.Length > 0 && EntityUid.TryParse(args[0], out var entityUid))
        {
            // Show actions for specific entity
            if (!_entityManager.TryGetComponent<COGRControlledComponent>(entityUid, out var controlled))
            {
                shell.WriteError($"Entity {entityUid} is not COGR-controlled");
                return;
            }

            var bodyId = BodyId.Parse($"body_{controlled.BodyId:N}");
            var actions = registry.GetActiveForBody(bodyId);

            shell.WriteLine($"Active actions for entity {entityUid}:");
            foreach (var action in actions)
            {
                shell.WriteLine($"  - {action.ProposalId}: {action.Capability} (state: {action.State})");
            }
        }
        else
        {
            // Show all active actions
            var concreteRegistry = registry as COGRActionRegistry;
            if (concreteRegistry == null)
            {
                shell.WriteError("Cannot access all actions - registry type mismatch");
                return;
            }
            
            var actions = concreteRegistry.GetAll().Where(a => !a.State.IsTerminal()).ToList();
            shell.WriteLine($"All active actions ({actions.Count}):");
            foreach (var action in actions)
            {
                shell.WriteLine($"  - {action.ProposalId}: {action.Capability} (state: {action.State}, body: {action.BodyId})");
            }
        }
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRIncrementGenerationCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;

    public override string Command => "cogr_increment_generation";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError("Usage: cogr_increment_generation <entityUid>");
            return;
        }

        if (!EntityUid.TryParse(args[0], out var entityUid))
        {
            shell.WriteError($"Invalid entity UID: {args[0]}");
            return;
        }

        if (!_entityManager.TryGetComponent<COGRControlledComponent>(entityUid, out var controlled))
        {
            shell.WriteError($"Entity {entityUid} is not COGR-controlled");
            return;
        }

        var executor = _entityManager.System<COGRActionExecutor>();
        var bodyId = BodyId.FromGuid(controlled.BodyId);

        var oldAuthority = executor.GetBodyAuthority(bodyId);
        executor.RevokeBodyAuthority(bodyId);
        var newAuthority = executor.GetBodyAuthority(bodyId);

        shell.WriteLine($"Body authority generation incremented for entity {entityUid}");
        shell.WriteLine($"  Old generation: {oldAuthority?.Generation ?? 0}");
        shell.WriteLine($"  New generation: {newAuthority?.Generation ?? 0}");
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRRegisterEntityCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;

    public override string Command => "cogr_register_entity";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError("Usage: cogr_register_entity <entityUid>");
            return;
        }

        if (!EntityUid.TryParse(args[0], out var entityUid))
        {
            shell.WriteError($"Invalid entity UID: {args[0]}");
            return;
        }

        if (!_entityManager.EntityExists(entityUid))
        {
            shell.WriteError($"Entity {entityUid} does not exist");
            return;
        }

        // Add or update COGRControlledComponent
        var controlled = _entityManager.EnsureComponent<COGRControlledComponent>(entityUid);
        
        // Generate new IDs if they don't exist
        bool wasUpdated = false;
        
        if (controlled.AgentId == Guid.Empty)
        {
            controlled.AgentId = Guid.CreateVersion7();
            wasUpdated = true;
        }
        
        if (controlled.BodyId == Guid.Empty)
        {
            controlled.BodyId = Guid.CreateVersion7();
            wasUpdated = true;
        }

        if (wasUpdated)
        {
            controlled.IsActive = true;
            _entityManager.Dirty(entityUid, controlled);
        }

        // Register body authority
        var executor = _entityManager.System<COGRActionExecutor>();
        var agentId = AgentId.FromGuid(controlled.AgentId);
        var bodyId = BodyId.FromGuid(controlled.BodyId);
        var connectionId = ConnectionId.FromGuid(Guid.Empty);

        executor.RegisterAgentBody(agentId, bodyId, connectionId);

        shell.WriteLine($"Entity {entityUid} registered with COGR:");
        shell.WriteLine($"  AgentId: {controlled.AgentId}");
        shell.WriteLine($"  BodyId: {controlled.BodyId}");
        shell.WriteLine($"  Body authority created");
    }
}