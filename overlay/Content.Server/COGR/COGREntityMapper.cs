using System.Linq;
using Robust.Shared.Log;

namespace Content.Server.COGR;

/// <summary>
/// Maps SS14 EntityUids to stable COGR AgentIds.
/// </summary>
/// <remarks>
/// SS14 EntityUids are runtime-specific and recycled between rounds.
/// COGR AgentIds (UUIDv7) are globally unique and stable.
///
/// This mapper:
/// - Assigns new AgentIds to SS14 entities
/// - Maintains bidirectional lookup
/// - Does NOT persist mappings across server restarts (per F0.5 scope)
/// - Can be extended for persistence in F1+
/// </remarks>
public sealed class COGREntityMapper
{
    private readonly ISawmill _sawmill;

    // SS14 EntityUid -> COGR AgentId
    private readonly Dictionary<EntityUid, Guid> _entityToAgent = new();

    // COGR AgentId -> SS14 EntityUid
    private readonly Dictionary<Guid, EntityUid> _agentToEntity = new();

    // Lock for thread safety (SS14 may access from multiple threads)
    private readonly object _lock = new();

    public COGREntityMapper(ISawmill sawmill)
    {
        _sawmill = sawmill ?? throw new ArgumentNullException(nameof(sawmill));
    }

    /// <summary>
    /// Gets the number of registered entities.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entityToAgent.Count;
            }
        }
    }

    /// <summary>
    /// Registers an SS14 entity with a specific COGR AgentId.
    /// If the entity is already registered, returns the existing AgentId.
    /// </summary>
    /// <param name="entityUid">The SS14 entity UID.</param>
    /// <param name="agentId">The COGR AgentId to use (optional). If null, generates a new one.</param>
    /// <returns>The assigned or existing COGR AgentId.</returns>
    public Guid RegisterEntity(EntityUid entityUid, Guid? agentId = null)
    {
        lock (_lock)
        {
            // Check if already registered
            if (_entityToAgent.TryGetValue(entityUid, out var existingId))
            {
                _sawmill.Debug("Entity {0} already registered as agent {1}", entityUid, existingId);
                return existingId;
            }

            // Use provided AgentId or generate new UUIDv7-style AgentId
            var newAgentId = agentId ?? Guid.CreateVersion7();

            _entityToAgent[entityUid] = newAgentId;
            _agentToEntity[newAgentId] = entityUid;

            _sawmill.Debug("Registered entity {0} as agent {1}", entityUid, newAgentId);
            return newAgentId;
        }
    }

    /// <summary>
    /// Unregisters an SS14 entity.
    /// </summary>
    /// <param name="entityUid">The SS14 entity UID.</param>
    /// <returns>True if the entity was unregistered; false if it wasn't registered.</returns>
    public bool UnregisterEntity(EntityUid entityUid)
    {
        lock (_lock)
        {
            if (!_entityToAgent.TryGetValue(entityUid, out var agentId))
            {
                return false;
            }

            _entityToAgent.Remove(entityUid);
            _agentToEntity.Remove(agentId);

            _sawmill.Debug("Unregistered entity {0} (was agent {1})", entityUid, agentId);
            return true;
        }
    }

    /// <summary>
    /// Unregisters an agent by AgentId.
    /// </summary>
    /// <param name="agentId">The COGR AgentId.</param>
    /// <returns>True if the agent was unregistered; false if it wasn't registered.</returns>
    public bool UnregisterAgent(Guid agentId)
    {
        lock (_lock)
        {
            if (!_agentToEntity.TryGetValue(agentId, out var entityUid))
            {
                return false;
            }

            _agentToEntity.Remove(agentId);
            _entityToAgent.Remove(entityUid);

            _sawmill.Debug("Unregistered agent {0} (was entity {1})", agentId, entityUid);
            return true;
        }
    }

    /// <summary>
    /// Gets the AgentId for an SS14 entity.
    /// </summary>
    /// <param name="entityUid">The SS14 entity UID.</param>
    /// <returns>The AgentId, or null if not registered.</returns>
    public Guid? GetAgentId(EntityUid entityUid)
    {
        lock (_lock)
        {
            return _entityToAgent.TryGetValue(entityUid, out var agentId) ? agentId : null;
        }
    }

    /// <summary>
    /// Gets the SS14 entity for an AgentId.
    /// </summary>
    /// <param name="agentId">The COGR AgentId.</param>
    /// <returns>The EntityUid, or null if not registered.</returns>
    public EntityUid? GetEntityUid(Guid agentId)
    {
        lock (_lock)
        {
            return _agentToEntity.TryGetValue(agentId, out var entityUid) ? entityUid : null;
        }
    }

    /// <summary>
    /// Checks if an SS14 entity is registered.
    /// </summary>
    public bool IsEntityRegistered(EntityUid entityUid)
    {
        lock (_lock)
        {
            return _entityToAgent.ContainsKey(entityUid);
        }
    }

    /// <summary>
    /// Checks if an AgentId is registered.
    /// </summary>
    public bool IsAgentRegistered(Guid agentId)
    {
        lock (_lock)
        {
            return _agentToEntity.ContainsKey(agentId);
        }
    }

    /// <summary>
    /// Gets all registered agent IDs.
    /// </summary>
    public IReadOnlyList<Guid> GetAllAgentIds()
    {
        lock (_lock)
        {
            return _agentToEntity.Keys.ToList();
        }
    }

    /// <summary>
    /// Gets all registered entity UIDs.
    /// </summary>
    public IReadOnlyList<EntityUid> GetAllEntityUids()
    {
        lock (_lock)
        {
            return _entityToAgent.Keys.ToList();
        }
    }

    /// <summary>
    /// Clears all mappings.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            var count = _entityToAgent.Count;
            _entityToAgent.Clear();
            _agentToEntity.Clear();

            if (count > 0)
            {
                _sawmill.Info("Cleared {0} entity-agent mappings", count);
            }
        }
    }

    /// <summary>
    /// Gets a snapshot of all mappings for diagnostics.
    /// </summary>
    public IReadOnlyDictionary<EntityUid, Guid> GetMappingSnapshot()
    {
        lock (_lock)
        {
            return new Dictionary<EntityUid, Guid>(_entityToAgent);
        }
    }
}
