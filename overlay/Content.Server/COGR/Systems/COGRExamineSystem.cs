using Content.Shared.COGR.Components;
using Content.Shared.Examine;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Handles examination text for COGR-controlled entities.
/// </summary>
/// <remarks>
/// F1 Scope:
/// - Overrides the standard "blank stare" / "no soul" examination messages
/// - Shows "Controlled by COGR" when the entity is actively controlled
/// - Shows "COGR disconnected" when the runtime connection is lost
/// - Prevents the entity from appearing as a ghost role target
/// </remarks>
public sealed partial class COGRExamineSystem : EntitySystem
{
    [Dependency] private ILogManager _logManager = default!;

    private COGRAdapterSystem? _adapter;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("cogr.examine");

        _adapter = EntityManager.System<COGRAdapterSystem>();

        // Subscribe to examination events for COGR-controlled entities
        SubscribeLocalEvent<COGRControlledComponent, ExaminedEvent>(OnExamined);

        _sawmill.Info("COGR Examine System initialized");
    }

    /// <summary>
    /// Refreshes the mind examination state for a COGR-controlled entity.
    /// This overrides the standard MindExamineSystem behavior.
    /// </summary>
    public void RefreshMindState(EntityUid uid, COGRControlledComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return;

        // If the entity has a MindExaminableComponent, we need to handle it specially
        // The standard system would show "catatonic" or "SSD" for entities without minds
        // We override this in OnExamined instead

        _sawmill.Debug("Refreshed COGR mind state for entity {0}, active: {1}", uid, component.IsActive);
    }

    private void OnExamined(EntityUid uid, COGRControlledComponent component, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        // Determine the COGR status message
        string message;
        string color;

        if (component.IsActive && (_adapter?.IsConnected ?? false))
        {
            // Entity is actively controlled by COGR
            color = "cyan";
            message = Loc.GetString("cogr-examined-controlled", ("ent", uid));
        }
        else if (!component.IsActive || !(_adapter?.IsConnected ?? false))
        {
            // COGR runtime is disconnected or entity is inactive
            color = "orange";
            message = Loc.GetString("cogr-examined-disconnected", ("ent", uid));
        }
        else
        {
            // Fallback - shouldn't normally reach here
            color = "gray";
            message = Loc.GetString("cogr-examined-unknown", ("ent", uid));
        }

        // Push the COGR status message
        args.PushMarkup($"[color={color}]{message}[/color]");

        _sawmill.Debug("Examined COGR entity {0}: {1}", uid, message);
    }

    /// <summary>
    /// Updates the active state of a COGR-controlled entity.
    /// Called when the COGR runtime connection state changes.
    /// </summary>
    public void SetEntityActive(EntityUid uid, bool active)
    {
        if (!TryComp<COGRControlledComponent>(uid, out var component))
            return;

        component.IsActive = active;
        Dirty(uid, component);

        _sawmill.Debug("Set COGR entity {0} active state to {1}", uid, active);
    }

    /// <summary>
    /// Updates all COGR-controlled entities when connection state changes.
    /// </summary>
    public void UpdateAllEntitiesConnectionState(bool connected)
    {
        var query = EntityQueryEnumerator<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            comp.IsActive = connected;
            Dirty(uid, comp);
        }

        _sawmill.Info("Updated all COGR entities to connected state: {0}", connected);
    }
}
