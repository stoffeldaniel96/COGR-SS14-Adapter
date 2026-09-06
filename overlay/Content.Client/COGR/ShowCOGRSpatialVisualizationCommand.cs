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
                shell.WriteLine("Calibration: perceivedLocal | belief|v|Local | actualTiles | actualCalibratedLocal (adapter body calibration).");

                foreach (var target in _visualization.Targets
                             .OrderByDescending(static target => target.IsFocal)
                             .ThenByDescending(static target => target.IsRichlyMaintained)
                             .ThenBy(static target => target.TargetId, StringComparer.Ordinal))
                {
                    var hasBeliefMinusPerceived = target.HasPerceivedLocalRange;
                    var beliefMinusPerceived = hasBeliefMinusPerceived
                        ? target.BeliefVectorMagnitudeLocalUnits - target.PerceivedLocalRange
                        : 0.0;
                    var hasPerceivedMinusActual = target.HasPerceivedLocalRange
                                                 && target.HasActualDistanceCalibratedLocalUnits;
                    var perceivedMinusActual = hasPerceivedMinusActual
                        ? target.PerceivedLocalRange - target.ActualDistanceCalibratedLocalUnits
                        : 0.0;
                    shell.WriteLine(
                        $"  target={target.TargetId} rev={target.TargetRevision} focal={target.IsFocal} rich={target.IsRichlyMaintained} "
                        + $"perceivedLocal={Format(target.HasPerceivedLocalRange, target.PerceivedLocalRange)} "
                        + $"belief|v|Local={target.BeliefVectorMagnitudeLocalUnits:F4} "
                        + $"actualTiles={Format(target.HasActualDistanceTiles, target.ActualDistanceTiles)} "
                        + $"actualCalibratedLocal={Format(target.HasActualDistanceCalibratedLocalUnits, target.ActualDistanceCalibratedLocalUnits)} "
                        + $"belief-perceived={Format(hasBeliefMinusPerceived, beliefMinusPerceived)} "
                        + $"perceived-actual={Format(hasPerceivedMinusActual, perceivedMinusActual)}");
                }
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
        shell.WriteLine("Run 'showcogrspatial' with no argument to inspect counts and the per-target calibration tuple.");
    }

    private static string Format(bool hasValue, double value) => hasValue ? value.ToString("F4") : "n/a";
}
