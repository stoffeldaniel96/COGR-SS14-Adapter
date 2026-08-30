using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Sequences;
using COGR.Core.Time;
using Content.Server.Administration;
using Content.Server.COGR.Actions;
using Content.Server.COGR.Systems;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Commands;

/// <summary>
/// Admin-only F3 acceptance fixture. Submits a normal interaction.open action using an opaque
/// reference returned by perception. The executor must resolve the target under the exact current
/// connection, agent/body authority, and body generation before native SS14 interaction occurs.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGROpenObservedTargetCommand : IConsoleCommand
{
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IGameTiming _timing = default!;

    public string Command => "cogr_f3_open_observed";
    public string Description =>
        "Opens a perceived door through strict opaque-reference action resolution.";
    public string Help => "cogr_f3_open_observed <agent-id> <environment-reference>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (!Guid.TryParse(args[0], out var agentGuid) || agentGuid == Guid.Empty)
        {
            shell.WriteError("agent-id must be an assigned UUID.");
            return;
        }

        if (!Guid.TryParse(args[1], out var referenceGuid) || referenceGuid == Guid.Empty)
        {
            shell.WriteError("environment-reference must be the UUID returned by cogr_runtime_inspect.");
            return;
        }

        var adapter = _systems.GetEntitySystem<COGRAdapterSystem>();
        var authority = _systems.GetEntitySystem<COGRBodyAuthorityCoordinatorSystem>();
        var executor = _systems.GetEntitySystem<COGRActionExecutor>();

        if (adapter.Connection is not { IsConnected: true } connection ||
            connection.ConnectionId == Guid.Empty)
        {
            shell.WriteError("COGR runtime is not currently connected.");
            return;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        var agentId = AgentId.FromGuid(agentGuid);
        var lease = authority.ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue)
        {
            shell.WriteError("No unambiguous current body authority exists for this agent.");
            return;
        }

        var tick = new SimTick((ulong)_timing.CurTick.Value);
        var parameters = ActionParameterSerializer.Serialize(new OpenActionParams
        {
            TargetRef = EnvironmentRef.FromGuid(referenceGuid),
            Force = false,
        });

        var attempt = ActionAttempt.Create(
            agentId,
            lease.Value.BodyId,
            lease.Value,
            ActionCapability.InteractionOpen,
            parameters,
            tick,
            new RuntimeSequence(1));

        var proposal = executor.ProposeAction(attempt);
        shell.WriteLine($"Action proposed: ProposalId={attempt.ProposalId}");
        shell.WriteLine($"Disposition: {(proposal.IsAccepted ? "Accepted" : "Rejected")}");

        if (!proposal.IsAccepted)
        {
            shell.WriteError($"{proposal.RejectionReason}: {proposal.Detail}");
            return;
        }

        var execution = executor.StartAction(attempt.ProposalId);
        if (!execution.IsSuccess)
        {
            shell.WriteError(
                $"Execution failed closed: {execution.FailureReason ?? ActionFailureReason.Unspecified} - " +
                $"{execution.Detail ?? "no detail"}");
            return;
        }

        if (execution.IsStarted)
        {
            shell.WriteLine("Door interaction started asynchronously.");
            return;
        }

        shell.WriteLine("Door interaction completed through strict opaque-reference resolution.");
    }
}
