using Robust.Shared.Console;

namespace Content.Client.COGR;

/// <summary>Toggles the admin-only COGR belief/path spatial overlay.</summary>
public sealed partial class ShowCOGRSpatialVisualizationCommand : LocalizedEntityCommands
{
    [Dependency] private COGRSpatialVisualizationSystem _visualization = default!;

    public override string Command => "showcogrspatial";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _visualization.Enabled ^= true;
        shell.WriteLine($"COGR spatial visualization {(_visualization.Enabled ? "enabled" : "disabled")}.");
    }
}
