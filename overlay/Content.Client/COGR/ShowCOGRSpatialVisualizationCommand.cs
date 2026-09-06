using Robust.Shared.Console;

namespace Content.Client.COGR;

/// <summary>Selects one exact Coggent for the admin-only spatial belief/path overlay.</summary>
public sealed partial class ShowCOGRSpatialVisualizationCommand : LocalizedEntityCommands
{
    [Dependency] private COGRSpatialVisualizationSystem _visualization = default!;

    public override string Command => "showcogrspatial";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteLine("Usage: showcogrspatial <agent-id|off>");
            if (_visualization.TrackedAgentId is { } current)
            {
                shell.WriteLine($"Currently tracking {current}.");
                shell.WriteLine(
                    $"Resident belief targets: {_visualization.ResidentTargetCount}; "
                    + $"world-space markers: {_visualization.ProjectedResidentTargetCount}; "
                    + $"unprojectable in current owner frame: {_visualization.UnprojectableResidentTargetCount}; "
                    + $"rich active maintenance: {_visualization.RichlyMaintainedTargetCount}.");
            }
            return;
        }

        if (string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase))
        {
            _visualization.StopTracking();
            shell.WriteLine("COGR spatial visualization disabled.");
            return;
        }

        if (!Guid.TryParse(args[0], out var agentGuid) || agentGuid == Guid.Empty)
        {
            shell.WriteLine("Agent id must be an assigned UUID, or use 'off'.");
            return;
        }

        var agentId = agentGuid.ToString("D");
        _visualization.TrackAgent(agentId);
        shell.WriteLine($"COGR spatial visualization tracking {agentId}.");
        shell.WriteLine("Resident spatial belief targets render blue; the explicit perceptual-attention focus renders red.");
        shell.WriteLine("Markers retire only when a successful Runtime full frame no longer reports that resident target.");
        shell.WriteLine("Run 'showcogrspatial' with no argument to inspect resident, projected, unprojectable, and rich-maintenance counts.");
    }
}
