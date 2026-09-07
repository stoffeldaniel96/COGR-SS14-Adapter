using System.Linq;
using Robust.Shared.Console;

namespace Content.Client.COGR;

/// <summary>Selects one exact Coggent for the admin-only spatial belief/path overlay.</summary>
public sealed partial class ShowCOGRSpatialVisualizationCommand : LocalizedEntityCommands
{
    [Dependency] private COGRSpatialVisualizationSystem _visualization = default!;

    public override string Command => "showcogrspatial";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            shell.WriteLine("Usage: showcogrspatial <agent-id> [target-id] | showcogrspatial off");
            if (_visualization.TrackedAgentId is { } current)
            {
                var selectedTarget = string.IsNullOrWhiteSpace(_visualization.TrackedTargetId)
                    ? "<none>"
                    : _visualization.TrackedTargetId;
                shell.WriteLine($"Currently tracking {current}; diagnostic target={selectedTarget}.");
                shell.WriteLine(
                    $"Resident belief targets: {_visualization.ResidentTargetCount}; "
                    + $"map markers: {_visualization.ProjectedResidentTargetCount}; "
                    + $"unprojectable in current owner frame: {_visualization.UnprojectableResidentTargetCount}; "
                    + $"rich active maintenance: {_visualization.RichlyMaintainedTargetCount}.");
                shell.WriteLine("Signed audit: perceivedLocal -> beliefLocal -> ownerNative -> parentOffset -> realizedMapDelta; privileged actualMapDelta is comparison only.");
                shell.WriteLine("Magnitude audit: perceivedLocal | belief|v|Local | expectedTiles | realizedTiles | actualTiles | actualCalibratedLocal.");

                foreach (var target in _visualization.Targets
                             .OrderByDescending(static target => target.IsFocal)
                             .ThenByDescending(static target => target.IsRichlyMaintained)
                             .ThenBy(static target => target.TargetId, StringComparer.Ordinal))
                {
                    var hasBeliefMinusPerceived = target.HasPerceivedLocalVector;
                    var beliefMinusPerceivedX = hasBeliefMinusPerceived
                        ? target.BeliefLocalX - target.PerceivedLocalX
                        : 0.0;
                    var beliefMinusPerceivedY = hasBeliefMinusPerceived
                        ? target.BeliefLocalY - target.PerceivedLocalY
                        : 0.0;
                    var hasRealizedMinusActual = target.HasActualMapDelta;
                    var realizedMinusActualX = hasRealizedMinusActual
                        ? target.BeliefRealizedMapDeltaX - target.ActualMapDeltaX
                        : 0.0;
                    var realizedMinusActualY = hasRealizedMinusActual
                        ? target.BeliefRealizedMapDeltaY - target.ActualMapDeltaY
                        : 0.0;

                    shell.WriteLine(
                        $"  target={target.TargetId} rev={target.TargetRevision} focal={target.IsFocal} rich={target.IsRichlyMaintained} "
                        + $"perceivedLocal={FormatVector(target.HasPerceivedLocalVector, target.PerceivedLocalX, target.PerceivedLocalY)} "
                        + $"sampleTick={(target.HasPerceivedLocalVector ? target.PerceivedSampleTick.ToString() : "n/a")} "
                        + $"sampleAgeTicks={(target.HasPerceivedLocalVector ? target.PerceivedSampleAgeTicks.ToString() : "n/a")} "
                        + $"beliefLocal=({target.BeliefLocalX:F4},{target.BeliefLocalY:F4}) "
                        + $"belief-perceived={FormatVector(hasBeliefMinusPerceived, beliefMinusPerceivedX, beliefMinusPerceivedY)} "
                        + $"ownerNative=({target.BeliefOwnerRelativeNativeX:F4},{target.BeliefOwnerRelativeNativeY:F4}) "
                        + $"bodyLocalRot={target.BodyLocalRotationRadians:F4}rad "
                        + $"parentOffset=({target.BeliefParentOffsetX:F4},{target.BeliefParentOffsetY:F4}) "
                        + $"realizedMapDelta=({target.BeliefRealizedMapDeltaX:F4},{target.BeliefRealizedMapDeltaY:F4}) "
                        + $"actualMapDelta={FormatVector(target.HasActualMapDelta, target.ActualMapDeltaX, target.ActualMapDeltaY)} "
                        + $"realized-actual={FormatVector(hasRealizedMinusActual, realizedMinusActualX, realizedMinusActualY)} "
                        + $"perceivedRange={Format(target.HasPerceivedLocalRange, target.PerceivedLocalRange)} "
                        + $"belief|v|Local={target.BeliefVectorMagnitudeLocalUnits:F4} "
                        + $"expectedTiles={target.BeliefExpectedDistanceTiles:F4} "
                        + $"realizedTiles={target.BeliefRealizedDistanceTiles:F4} "
                        + $"actualTiles={Format(target.HasActualDistanceTiles, target.ActualDistanceTiles)} "
                        + $"actualCalibratedLocal={Format(target.HasActualDistanceCalibratedLocalUnits, target.ActualDistanceCalibratedLocalUnits)}");
                }
            }
            return;
        }

        if (string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 1)
            {
                shell.WriteLine("Usage: showcogrspatial off");
                return;
            }

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
        var targetId = args.Length == 2 && !string.IsNullOrWhiteSpace(args[1])
            ? args[1].Trim()
            : null;
        _visualization.TrackAgent(agentId, targetId);
        shell.WriteLine($"COGR spatial visualization tracking {agentId}.");
        if (targetId is not null)
            shell.WriteLine($"Bounded server spatial telemetry selected target {targetId}.");
        else
            shell.WriteLine("No server telemetry target selected; overlay still renders the complete Runtime visualization frame.");
        shell.WriteLine("Belief targets render blue; explicit perceptual focus renders red; resolved Coggent body origin renders cyan; privileged current actual referent renders yellow.");
        shell.WriteLine("Markers retire only when a successful Runtime full frame no longer reports that resident target.");
        shell.WriteLine("Run 'showcogrspatial' with no argument to inspect TargetIds, then rerun with one target-id to enable bounded server trace telemetry.");
    }

    private static string Format(bool hasValue, double value) => hasValue ? value.ToString("F4") : "n/a";

    private static string FormatVector(bool hasValue, double x, double y) =>
        hasValue ? $"({x:F4},{y:F4})" : "n/a";
}
