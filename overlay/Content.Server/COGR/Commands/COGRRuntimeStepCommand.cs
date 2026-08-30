using System.Text.Json;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Server.COGR.Commands;

/// <summary>
/// Sends a diagnostic movement.step request through the complete runtime duplex path.
/// </summary>
public sealed class COGRRuntimeStepCommand : IConsoleCommand
{
    public string Command => "cogr_runtime_step";
    public string Description => "Requests one COGR-controlled agent step through the runtime duplex stream.";
    public string Help => "cogr_runtime_step <agent-id> <direction> [distance] [run]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 2 or > 4)
        {
            shell.WriteError(Help);
            return;
        }

        if (!Guid.TryParse(args[0], out var agentId) || agentId == Guid.Empty)
        {
            shell.WriteError("agent-id must be an assigned UUID.");
            return;
        }

        var direction = args[1];
        var distance = 1.0;
        if (args.Length >= 3 && (!double.TryParse(args[2], out distance) || distance <= 0))
        {
            shell.WriteError("distance must be a positive number.");
            return;
        }

        var run = false;
        if (args.Length >= 4 && !bool.TryParse(args[3], out run))
        {
            shell.WriteError("run must be true or false.");
            return;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        var adapter = systems.GetEntitySystem<COGRAdapterSystem>();
        if (adapter.Connection is not { IsConnected: true } connection)
        {
            shell.WriteError("COGR runtime is not connected.");
            return;
        }

        var parameters = JsonSerializer.SerializeToUtf8Bytes(new
        {
            agentId = agentId.ToString("D"),
            direction,
            distance,
            run,
            priority = 100,
        });

        var correlationId = connection.SendAdministrativeCommand(
            "cogr.f2_5.test_step",
            parameters);

        shell.WriteLine(
            $"Queued runtime-originated step for agent {agentId:D}; correlation {correlationId:D}.");
    }
}
