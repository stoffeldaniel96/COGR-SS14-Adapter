using Content.Server.Ghost.Roles.Components;
using Content.Server.Mind;
using Content.Shared.COGR.Components;
using Content.Shared.Ghost.Roles.Components;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.SSDIndicator;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Handles mind and session-related overrides for COGR-controlled entities.
/// </summary>
/// <remarks>
/// F1 Scope:
/// - Prevents COGR-controlled entities from being assigned ghost roles
/// - Ensures entities remain controllable without a player session or mind
/// - Overrides standard "no mind" behavior for COGR entities
/// - Removes ghost role components from spawned COGR humanoids
///
/// COGR entities operate without traditional SS14 minds because they are
/// controlled by the external COGR runtime, not by player sessions.
/// </remarks>
public sealed partial class COGRMindOverrideSystem : EntitySystem
{
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private MindSystem _mindSystem = default!;

    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("cogr.mind");

        // Intercept mind examination to provide COGR-specific behavior
        SubscribeLocalEvent<COGRControlledComponent, MindAddedMessage>(OnMindAdded);

        _sawmill.Info("COGR Mind Override System initialized");
    }

    /// <summary>
    /// Configures an entity for COGR control by removing ghost roles and disabling SSD indicators.
    /// Called by COGRBodyRegistrationSystem during ComponentStartup.
    /// </summary>
    public void ConfigureEntityForCOGRControl(EntityUid uid)
    {
        // When a COGR controlled entity starts up, configure it for external control

        // Remove any ghost role components to prevent ghost takeover
        RemoveGhostRoleComponents(uid);

        // Ensure the entity doesn't show as available for ghost roles
        EnsureNoGhostRole(uid);

        // Override SSD indicator - COGR entities are not SSD even without a player
        DisableSSDIndicator(uid);

        _sawmill.Debug("Configured COGR entity {0} for external control", uid);
    }

    private void OnMindAdded(EntityUid uid, COGRControlledComponent component, MindAddedMessage args)
    {
        // A mind was added to a COGR-controlled entity
        // This shouldn't normally happen, but handle it gracefully
        _sawmill.Warning("Mind was added to COGR-controlled entity {0} - this is unexpected", uid);

        // We could remove the mind here, but that might cause issues
        // Instead, log it and let the COGR system handle it
    }

    /// <summary>
    /// Removes ghost role components from an entity.
    /// </summary>
    private void RemoveGhostRoleComponents(EntityUid uid)
    {
        // Remove GhostRoleComponent if present
        if (HasComp<GhostRoleComponent>(uid))
        {
            RemComp<GhostRoleComponent>(uid);
            _sawmill.Debug("Removed GhostRoleComponent from COGR entity {0}", uid);
        }

        // Remove GhostTakeoverAvailableComponent if present
        if (HasComp<GhostTakeoverAvailableComponent>(uid))
        {
            RemComp<GhostTakeoverAvailableComponent>(uid);
            _sawmill.Debug("Removed GhostTakeoverAvailableComponent from COGR entity {0}", uid);
        }
    }

    /// <summary>
    /// Ensures an entity is not registered as a ghost role.
    /// </summary>
    private void EnsureNoGhostRole(EntityUid uid)
    {
        // The GhostRoleSystem maintains a list of available ghost roles
        // We need to ensure COGR entities are not in that list

        // This is handled by removing the relevant components
        // The GhostRoleSystem should automatically unregister entities
        // when their GhostRoleComponent is removed
    }

    /// <summary>
    /// Disables the SSD indicator for COGR-controlled entities.
    /// COGR entities are controlled externally and should not show as SSD.
    /// </summary>
    private void DisableSSDIndicator(EntityUid uid)
    {
        if (TryComp<SSDIndicatorComponent>(uid, out var ssd))
        {
            ssd.IsSSD = false;
            Dirty(uid, ssd);
            _sawmill.Debug("Disabled SSD indicator for COGR entity {0}", uid);
        }

        // Remove MindExaminableComponent to prevent "catatonic" message
        // COGR entities don't have traditional minds but aren't catatonic
        if (HasComp<MindExaminableComponent>(uid))
        {
            RemComp<MindExaminableComponent>(uid);
            _sawmill.Debug("Removed MindExaminableComponent from COGR entity {0}", uid);
        }
    }

    /// <summary>
    /// Updates the SSD indicator state for a COGR entity based on connection status.
    /// </summary>
    public void UpdateSSDState(EntityUid uid, bool isConnected)
    {
        if (!TryComp<COGRControlledComponent>(uid, out var cogr))
            return;

        if (TryComp<SSDIndicatorComponent>(uid, out var ssd))
        {
            // When COGR is connected and active, entity is not SSD
            // When disconnected, could show as SSD (optional - depends on desired behavior)
            ssd.IsSSD = !isConnected || !cogr.IsActive;
            Dirty(uid, ssd);
        }
    }

    /// <summary>
    /// Checks if an entity is COGR-controlled and should bypass normal mind checks.
    /// </summary>
    public bool IsCOGRControlled(EntityUid uid)
    {
        return HasComp<COGRControlledComponent>(uid);
    }

    /// <summary>
    /// Checks if an entity should be treated as "having a mind" for gameplay purposes.
    /// COGR-controlled entities are considered to have minds even without a MindComponent.
    /// </summary>
    public bool HasEffectiveMind(EntityUid uid)
    {
        // Check for actual mind first
        if (_mindSystem.TryGetMind(uid, out _, out _))
            return true;

        // COGR-controlled entities are treated as having minds
        if (TryComp<COGRControlledComponent>(uid, out var cogr) && cogr.IsActive)
            return true;

        return false;
    }
}
