using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Maximum number of COGR-controlled bodies that automated population spawning may create
    /// for a round. Explicit anchor/test spawns are not charged against this budget.
    /// </summary>
    public static readonly CVarDef<int> COGRPopulationSpawnBudget =
        CVarDef.Create("cogr.population.spawn_budget", 0, CVar.SERVERONLY);

    /// <summary>
    /// Requested Passenger allotment within <see cref="COGRPopulationSpawnBudget"/>.
    /// The effective Passenger count is capped by the remaining global population budget and
    /// by the number of eligible, unobstructed spawn points.
    /// </summary>
    public static readonly CVarDef<int> COGRPopulationPassengerAllotment =
        CVarDef.Create("cogr.population.roles.passenger", 0, CVar.SERVERONLY);

    /// <summary>
    /// Enables the legacy F1 map-anchor spawner. Headless population acceptance disables this
    /// so explicit COGRAgentAnchor fixtures do not contaminate configured population counts.
    /// </summary>
    public static readonly CVarDef<bool> COGRLegacyAnchorSpawningEnabled =
        CVarDef.Create("cogr.legacy_anchor_spawning_enabled", true, CVar.SERVERONLY);
}
