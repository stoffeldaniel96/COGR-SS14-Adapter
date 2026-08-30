using COGR.Core.Identifiers;
using Content.Server.COGR.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Server.COGR.Commands;

public sealed class COGRMakeBodyHungryCommand : IConsoleCommand
{
    public string Command => "make_body_hungry";
    public string Description => "Sets one COGR-controlled body's native hunger inside the first player-visible hunger band.";
    public string Help => "make_body_hungry <body-id>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryResolveBody(shell, args, out var body))
            return;

        var entityManager = IoCManager.Resolve<IEntityManager>();
        if (!entityManager.TryGetComponent<HungerComponent>(body, out var hunger))
        {
            shell.WriteError("The selected body has no native hunger channel.");
            return;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        var hungerSystem = systems.GetEntitySystem<HungerSystem>();
        var peckish = hunger.Thresholds[HungerThreshold.Peckish];
        var starving = hunger.Thresholds[HungerThreshold.Starving];
        hungerSystem.SetHunger(body, (peckish + starving) / 2f, hunger);
        shell.WriteLine("Set native body hunger inside the Peckish band. Any COGR sensory evidence is produced by the normal threshold-change path.");
    }

    private static bool TryResolveBody(IConsoleShell shell, string[] args, out EntityUid body)
    {
        body = default;
        if (args.Length != 1)
        {
            shell.WriteError("make_body_hungry <body-id>");
            return false;
        }

        if (!Guid.TryParse(args[0], out var bodyGuid) || bodyGuid == Guid.Empty)
        {
            shell.WriteError("body-id must be an assigned UUID.");
            return false;
        }

        var index = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<COGRBodyBindingIndexSystem>();
        if (!index.TryGetUniqueEntity(BodyId.FromGuid(bodyGuid), out body))
        {
            shell.WriteError("No unique COGR-controlled body matches that body-id.");
            return false;
        }

        return true;
    }
}

public sealed class COGRMakeBodyThirstyCommand : IConsoleCommand
{
    public string Command => "make_body_thirsty";
    public string Description => "Sets one COGR-controlled body's native thirst inside a selected player-visible thirst band.";
    public string Help => "make_body_thirsty <body-id> [thirsty|parched]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryResolveBody(shell, args, out var body))
            return;

        var entityManager = IoCManager.Resolve<IEntityManager>();
        if (!entityManager.TryGetComponent<ThirstComponent>(body, out var thirst))
        {
            shell.WriteError("The selected body has no native thirst channel.");
            return;
        }

        var requestedBand = args.Length == 2 ? args[1] : "thirsty";
        float target;
        string bandName;
        if (requestedBand.Equals("thirsty", StringComparison.OrdinalIgnoreCase))
        {
            var thirsty = thirst.ThirstThresholds[ThirstThreshold.Thirsty];
            var parched = thirst.ThirstThresholds[ThirstThreshold.Parched];
            target = (thirsty + parched) / 2f;
            bandName = "Thirsty";
        }
        else if (requestedBand.Equals("parched", StringComparison.OrdinalIgnoreCase))
        {
            var parched = thirst.ThirstThresholds[ThirstThreshold.Parched];
            var dead = thirst.ThirstThresholds[ThirstThreshold.Dead];
            target = (parched + dead) / 2f;
            bandName = "Parched";
        }
        else
        {
            shell.WriteError(Help);
            return;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        var thirstSystem = systems.GetEntitySystem<ThirstSystem>();
        thirstSystem.SetThirst(body, thirst, target);
        shell.WriteLine($"Set native body thirst inside the {bandName} band. The native nutrition tick will publish the threshold transition normally.");
    }

    private static bool TryResolveBody(IConsoleShell shell, string[] args, out EntityUid body)
    {
        body = default;
        if (args.Length is < 1 or > 2)
        {
            shell.WriteError("make_body_thirsty <body-id> [thirsty|parched]");
            return false;
        }

        if (!Guid.TryParse(args[0], out var bodyGuid) || bodyGuid == Guid.Empty)
        {
            shell.WriteError("body-id must be an assigned UUID.");
            return false;
        }

        var index = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<COGRBodyBindingIndexSystem>();
        if (!index.TryGetUniqueEntity(BodyId.FromGuid(bodyGuid), out body))
        {
            shell.WriteError("No unique COGR-controlled body matches that body-id.");
            return false;
        }

        return true;
    }
}
