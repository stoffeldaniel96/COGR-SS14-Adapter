using Content.Server.COGR.Components;
using Content.Shared.Tag;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Handles registration of COGR agents spawned from legacy anchors.
/// </summary>
/// <remarks>
/// <para>
/// COGRAgentAnchor tags trigger humanoid spawning through
/// <see cref="COGREntitySpawnerSystem"/>. This system retains the legacy
/// <see cref="COGRAgentComponent"/> shutdown path and the explicit registration API used by
/// the spawner.
/// </para>
/// <para>
/// <see cref="Content.Shared.COGR.Components.COGRControlledComponent"/> lifecycle is owned
/// exclusively by <see cref="COGRBodyAuthorityCoordinatorSystem"/>. Robust permits only one
/// component lifecycle subscription for a component/event pair, so controlled-body cleanup
/// must not be split across systems.
/// </para>
/// </remarks>
public sealed partial class COGRAgentRegistrationSystem : EntitySystem
{
    [Dependency] private TagSystem _tagSystem = default!;
    [Dependency] private ILogManager _logManager = default!;

    private COGRAdapterSystem? _adapter;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("cogr.agent.registration");
        _adapter = EntityManager.System<COGRAdapterSystem>();

        // Legacy anchor/marker lifecycle only. Controlled-body startup and shutdown are
        // consolidated in COGRBodyAuthorityCoordinatorSystem.
        SubscribeLocalEvent<COGRAgentComponent, ComponentShutdown>(OnAgentComponentShutdown);

        _sawmill.Info("COGR Agent Registration System initialized (F1)");
    }

    private void OnAgentComponentShutdown(
        EntityUid uid,
        COGRAgentComponent component,
        ComponentShutdown args)
    {
        UnregisterAgent(uid);
        _sawmill.Info(
            "Unregistered COGR agent (legacy): Entity {0} -> Agent {1}",
            uid,
            component.AgentId);
    }

    private void UnregisterAgent(EntityUid uid)
    {
        _adapter?.UnregisterAgent(uid);
    }

    /// <summary>
    /// Manually registers an entity as a COGR agent.
    /// Used by <see cref="COGREntitySpawnerSystem"/> for spawned humanoids.
    /// </summary>
    public Guid? RegisterEntity(EntityUid uid, string? displayName = null)
    {
        if (_adapter == null || !_adapter.IsEnabled)
        {
            _sawmill.Warning("Cannot register entity: adapter not available or disabled");
            return null;
        }

        var agentId = _adapter.RegisterAgent(uid);
        if (agentId == null)
        {
            _sawmill.Warning("Failed to register entity {0} with adapter", uid);
            return null;
        }

        _sawmill.Info(
            "Registered entity {0} ({1}) as COGR agent {2}",
            uid,
            displayName ?? Name(uid),
            agentId);

        return agentId;
    }
}
