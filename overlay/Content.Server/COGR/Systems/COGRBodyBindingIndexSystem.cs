using System;
using System.Collections.Generic;
using COGR.Core.Identifiers;
using Content.Shared.COGR.Components;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Maintains identity indexes for controlled COGR bodies so adapter systems can resolve stable
/// body identity without repeatedly scanning every controlled entity.
/// </summary>
/// <remarks>
/// Membership is driven by <see cref="COGRBodyRegistrationSystem"/> during ComponentInit and
/// ComponentRemove. Authority startup/shutdown remains exclusively owned by
/// <see cref="COGRBodyAuthorityCoordinatorSystem"/>.
/// </remarks>
public sealed partial class COGRBodyBindingIndexSystem : EntitySystem
{
    private readonly Dictionary<AgentId, HashSet<EntityUid>> _entitiesByAgent = new();
    private readonly Dictionary<BodyId, HashSet<EntityUid>> _entitiesByBody = new();
    private readonly HashSet<EntityUid> _controlledEntities = new();

    public bool TryGetUniqueEntity(AgentId agentId, out EntityUid entity)
    {
        entity = default;
        if (!_entitiesByAgent.TryGetValue(agentId, out var entities) || entities.Count != 1)
            return false;

        foreach (var uid in entities)
        {
            entity = uid;
            return true;
        }

        return false;
    }

    public bool TryGetUniqueEntity(BodyId bodyId, out EntityUid entity)
    {
        entity = default;
        if (!_entitiesByBody.TryGetValue(bodyId, out var entities) || entities.Count != 1)
            return false;

        foreach (var uid in entities)
        {
            entity = uid;
            return true;
        }

        return false;
    }

    public IReadOnlyCollection<EntityUid> ControlledEntities => _controlledEntities;

    public void RegisterBody(EntityUid uid, COGRControlledComponent component)
    {
        if (component.AgentId == Guid.Empty || component.BodyId == Guid.Empty)
            return;

        _controlledEntities.Add(uid);
        Add(_entitiesByAgent, AgentId.FromGuid(component.AgentId), uid);
        Add(_entitiesByBody, BodyId.FromGuid(component.BodyId), uid);
    }

    public void UnregisterBody(EntityUid uid, COGRControlledComponent component)
    {
        _controlledEntities.Remove(uid);
        if (component.AgentId != Guid.Empty)
            Remove(_entitiesByAgent, AgentId.FromGuid(component.AgentId), uid);
        if (component.BodyId != Guid.Empty)
            Remove(_entitiesByBody, BodyId.FromGuid(component.BodyId), uid);
    }

    private static void Add<TKey>(Dictionary<TKey, HashSet<EntityUid>> index, TKey key, EntityUid uid)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var entities))
        {
            entities = new HashSet<EntityUid>();
            index.Add(key, entities);
        }

        entities.Add(uid);
    }

    private static void Remove<TKey>(Dictionary<TKey, HashSet<EntityUid>> index, TKey key, EntityUid uid)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var entities))
            return;

        entities.Remove(uid);
        if (entities.Count == 0)
            index.Remove(key);
    }
}
