using System;
using Robust.Shared.Console;

namespace Content.Server.COGR.Commands;

/// <summary>
/// Enables or disables high-volume adapter origin tracing for the current server process.
/// </summary>
public sealed class COGRAdapterTraceCommand : IConsoleCommand
{
    public string Command => "cogr_adapter_trace";
    public string Description =>
        "Enables or disables [AUTO]/[PROMPTED] COGR adapter diagnostics for this server session.";
    public string Help => "cogr_adapter_trace [on|off|status]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError(Help);
            return;
        }

        if (args.Length == 0 || args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            shell.WriteLine(
                $"COGR adapter origin trace is {(COGRAdapterTrace.Enabled ? "ON" : "OFF")}. " +
                "It resets to OFF on server restart.");
            return;
        }

        if (args[0].Equals("on", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            COGRAdapterTrace.Enabled = true;
            shell.WriteLine("COGR adapter origin trace enabled for this server session.");
            return;
        }

        if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            COGRAdapterTrace.Enabled = false;
            shell.WriteLine("COGR adapter origin trace disabled.");
            return;
        }

        shell.WriteError(Help);
    }
}
