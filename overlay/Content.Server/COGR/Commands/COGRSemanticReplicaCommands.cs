using COGR.Core.Identifiers;
using Content.Server.COGR.Systems;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Server.COGR.Commands;

/// <summary>
/// Displays the bounded observer semantic replicas currently projected by Station.
/// </summary>
public sealed class COGRSemanticReplicaStatusCommand : IConsoleCommand
{
    public string Command => "cogr_replica_status";
    public string Description =>
        "Displays Station's current observer semantic replica scopes and sequences.";
    public string Help => "cogr_replica_status";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        var replicas = systems
            .GetEntitySystem<COGRSemanticReplicaSystem>()
            .GetDiagnosticStates();
        if (replicas.Count == 0)
        {
            shell.WriteLine("No active COGR semantic replicas are currently projected.");
            return;
        }

        foreach (var replica in replicas)
        {
            shell.WriteLine(
                $"connection={replica.ConnectionId} agent={replica.AgentId} " +
                $"body={replica.BodyId} generation={replica.BodyGeneration} " +
                $"sequence={replica.Sequence.Value} observations={replica.ObservationCount} " +
                $"gapArmed={replica.SkipNextSequence}");
        }
    }
}

/// <summary>
/// Arms a one-shot missing-delta fixture for one active observer replica.
/// </summary>
public sealed class COGRSemanticReplicaSkipNextCommand : IConsoleCommand
{
    public string Command => "cogr_replica_skip_next";
    public string Description =>
        "Skips one observer sequence so the next semantic change exercises runtime resynchronization.";
    public string Help => "cogr_replica_skip_next <agent-id>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (!Guid.TryParse(args[0], out var agentGuid) || agentGuid == Guid.Empty)
        {
            shell.WriteError("agent-id must be an assigned UUID.");
            return;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        var replicas = systems.GetEntitySystem<COGRSemanticReplicaSystem>();
        if (!replicas.SkipNextDelta(AgentId.FromGuid(agentGuid)))
        {
            shell.WriteError(
                "No active semantic replica exists for that agent. Wait for the initial baseline and retry.");
            return;
        }

        shell.WriteLine(
            $"Armed one semantic replica sequence gap for agent {agentGuid:D}. " +
            "Cause a visible semantic change, such as opening a nearby door or moving relative to an observed object. " +
            "The runtime should request a bounded replacement baseline automatically.");
    }
}
