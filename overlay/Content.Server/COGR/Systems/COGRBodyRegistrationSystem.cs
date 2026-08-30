using Content.Shared.COGR.Components;
using Content.Shared.Speech.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Assigns stable COGR agent and body identities during controlled-component initialization.
/// </summary>
/// <remarks>
/// Identity/index membership uses ComponentInit/ComponentRemove. Authority startup/shutdown is
/// intentionally separate: <see cref="COGRBodyAuthorityCoordinatorSystem"/> remains the single
/// owner of controlled-component ComponentStartup/ComponentShutdown.
/// </remarks>
public sealed class COGRBodyRegistrationSystem : EntitySystem
{
    private COGRBodyBindingIndexSystem _bodyIndex = default!;

    public override void Initialize()
    {
        base.Initialize();
        _bodyIndex = EntityManager.System<COGRBodyBindingIndexSystem>();
        SubscribeLocalEvent<COGRControlledComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<COGRControlledComponent, ComponentRemove>(OnComponentRemove);
    }

    private void OnComponentInit(
        EntityUid uid,
        COGRControlledComponent component,
        ComponentInit args)
    {
        EnsureComp<ActiveListenerComponent>(uid);

        if (component.AgentId == Guid.Empty)
        {
            component.AgentId = Guid.CreateVersion7();
            Dirty(uid, component);
        }

        if (component.BodyId == Guid.Empty)
        {
            component.BodyId = Guid.CreateVersion7();
            Dirty(uid, component);
        }

        _bodyIndex.RegisterBody(uid, component);
        if (EntityManager.TrySystem<COGRSemanticReplicaSystem>(out var semanticReplica))
            semanticReplica.NotifyControlledBodyMembershipChanged();

        Log.Info(
            $"Configured COGR passive listener for agent {component.AgentId}, body {component.BodyId}, entity {uid}");
    }

    private void OnComponentRemove(
        EntityUid uid,
        COGRControlledComponent component,
        ComponentRemove args)
    {
        _bodyIndex.UnregisterBody(uid, component);

        if (EntityManager.TrySystem<COGRSemanticReplicaSystem>(out var semanticReplica))
            semanticReplica.NotifyControlledBodyMembershipChanged();
        if (EntityManager.TrySystem<COGREmbodimentSupportSystem>(out var embodimentSupport))
            embodimentSupport.NotifyControlledBodyRemoved(component);
    }
}
