using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
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
/// Simplified admin commands for F02 action testing that don't require JSON.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRTurnCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override string Command => "cogr_turn";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError("Usage: cogr_turn <entityUid> <direction>");
            shell.WriteError("Directions: north, south, east, west, northeast, northwest, southeast, southwest");
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

        // Parse direction
        if (!Enum.TryParse<global::COGR.Core.Actions.Parameters.Direction>(args[1], true, out var direction))
        {
            shell.WriteError($"Invalid direction: {args[1]}");
            return;
        }

        // Create parameters
        var parameters = new TurnActionParams { TargetDirection = direction };
        var parametersBytes = ActionParameterSerializer.Serialize(parameters);

        // Execute action
        ProposeAndExecuteAction(shell, entityUid, controlled, ActionCapability.MovementTurn, parametersBytes);
    }

    private void ProposeAndExecuteAction(
        IConsoleShell shell,
        EntityUid entityUid,
        COGRControlledComponent controlled,
        ActionCapability capability,
        ReadOnlyMemory<byte> parameters)
    {
        var executor = _entityManager.System<COGRActionExecutor>();
        var agentId = AgentId.Parse($"agent_{controlled.AgentId:N}");
        var bodyId = BodyId.Parse($"body_{controlled.BodyId:N}");
        var currentTick = new SimTick((ulong)_timing.CurTick.Value);

        // Get or create body authority
        var authority = executor.GetBodyAuthority(bodyId);
        if (authority == null)
        {
            var connectionId = ConnectionId.Parse("conn_00000000000000000000000000000000");
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
            Capability = capability,
            Parameters = parameters,
            ParameterFormat = "json",
            TimeoutMs = 0
        };

        var proposalResult = executor.ProposeAction(attempt);
        shell.WriteLine($"ProposalId: {attempt.ProposalId}");
        shell.WriteLine($"Disposition: {(proposalResult.IsAccepted ? "Accepted" : "Rejected")}");

        if (!proposalResult.IsAccepted)
        {
            shell.WriteLine($"Reason: {proposalResult.RejectionReason} - {proposalResult.Detail}");
            return;
        }

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
}

[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRStepCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override string Command => "cogr_step";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError("Usage: cogr_step <entityUid> <direction> [distance]");
            shell.WriteError("Directions: north, south, east, west, northeast, northwest, southeast, southwest");
            shell.WriteError("Distance: optional, defaults to 1.0");
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

        if (!Enum.TryParse<global::COGR.Core.Actions.Parameters.Direction>(args[1], true, out var direction))
        {
            shell.WriteError($"Invalid direction: {args[1]}");
            return;
        }

        var distance = 1.0;
        if (args.Length > 2 && !double.TryParse(args[2], out distance))
        {
            shell.WriteError($"Invalid distance: {args[2]}");
            return;
        }

        var parameters = new StepActionParams { Direction = direction, Distance = distance };
        var parametersBytes = ActionParameterSerializer.Serialize(parameters);

        var executor = _entityManager.System<COGRActionExecutor>();
        var agentId = AgentId.Parse($"agent_{controlled.AgentId:N}");
        var bodyId = BodyId.Parse($"body_{controlled.BodyId:N}");
        var currentTick = new SimTick((ulong)_timing.CurTick.Value);

        var authority = executor.GetBodyAuthority(bodyId);
        if (authority == null)
        {
            var connectionId = ConnectionId.Parse("conn_00000000000000000000000000000000");
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
            Capability = ActionCapability.MovementStep,
            Parameters = parametersBytes,
            ParameterFormat = "json",
            TimeoutMs = 0
        };

        var proposalResult = executor.ProposeAction(attempt);
        shell.WriteLine($"ProposalId: {attempt.ProposalId}");
        shell.WriteLine($"Disposition: {(proposalResult.IsAccepted ? "Accepted" : "Rejected")}");

        if (!proposalResult.IsAccepted)
        {
            shell.WriteLine($"Reason: {proposalResult.RejectionReason} - {proposalResult.Detail}");
            return;
        }

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
}

[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRMoveToCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override string Command => "cogr_moveto";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 3)
        {
            shell.WriteError("Usage: cogr_moveto <entityUid> <x> <y> [run]");
            shell.WriteError("Example: cogr_moveto 42 10.5 5.0");
            shell.WriteError("Example: cogr_moveto 42 10.5 5.0 true");
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

        if (!double.TryParse(args[1], out var x))
        {
            shell.WriteError($"Invalid X coordinate: {args[1]}");
            return;
        }

        if (!double.TryParse(args[2], out var y))
        {
            shell.WriteError($"Invalid Y coordinate: {args[2]}");
            return;
        }

        var run = false;
        if (args.Length > 3 && !bool.TryParse(args[3], out run))
        {
            shell.WriteError($"Invalid run flag: {args[3]}");
            return;
        }

        var parameters = new MoveToLocationParams
        {
            TargetLocation = WorldLocation.Create(x, y),
            Run = run,
            ArrivalTolerance = 0.5
        };
        var parametersBytes = ActionParameterSerializer.Serialize(parameters);

        var executor = _entityManager.System<COGRActionExecutor>();
        var agentId = AgentId.Parse($"agent_{controlled.AgentId:N}");
        var bodyId = BodyId.Parse($"body_{controlled.BodyId:N}");
        var currentTick = new SimTick((ulong)_timing.CurTick.Value);

        var authority = executor.GetBodyAuthority(bodyId);
        if (authority == null)
        {
            var connectionId = ConnectionId.Parse("conn_00000000000000000000000000000000");
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
            Capability = ActionCapability.MovementMoveToLocation,
            Parameters = parametersBytes,
            ParameterFormat = "json",
            TimeoutMs = 0
        };

        var proposalResult = executor.ProposeAction(attempt);
        shell.WriteLine($"ProposalId: {attempt.ProposalId}");
        shell.WriteLine($"Disposition: {(proposalResult.IsAccepted ? "Accepted" : "Rejected")}");

        if (!proposalResult.IsAccepted)
        {
            shell.WriteLine($"Reason: {proposalResult.RejectionReason} - {proposalResult.Detail}");
            return;
        }

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
}