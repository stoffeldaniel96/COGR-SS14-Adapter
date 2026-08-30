using System.Linq;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Time;
using Robust.Shared.Log;

namespace Content.Server.COGR;

/// <summary>
/// SS14-specific opaque environment-reference registry.
/// Maps opaque references to Station entities while retaining enough adapter-only scope
/// metadata to invalidate the references without exposing raw entity identifiers.
/// </summary>
public sealed class COGRReferenceRegistry
{
    private readonly object _metadataLock = new();
    private readonly ISawmill _sawmill;
    private readonly IEntityManager _entityManager;
    private readonly EnvironmentReferenceRegistry<EntityUid> _registry;
    private readonly Dictionary<EnvironmentRef, StationReferenceMetadata> _metadata = new();

    public COGRReferenceRegistry(ISawmill sawmill, IEntityManager entityManager)
    {
        _sawmill = sawmill ?? throw new ArgumentNullException(nameof(sawmill));
        _entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
        _registry = new EnvironmentReferenceRegistry<EntityUid>();
    }

    /// <summary>
    /// Issues an opaque reference and records its Station-local lifecycle ownership.
    /// </summary>
    public EnvironmentRef IssueReference(
        EntityUid entity,
        ReferenceScope scope,
        string? category = null,
        AgentId? agentId = null)
    {
        var environmentReference = _registry.IssueReference(entity, scope, category);
        lock (_metadataLock)
        {
            _metadata[environmentReference] = new StationReferenceMetadata(
                environmentReference,
                entity,
                scope,
                agentId,
                category);
        }

        _sawmill.Debug(
            "Issued reference {0} for entity {1} (category: {2}, agent: {3})",
            environmentReference,
            entity,
            category ?? "none",
            agentId?.ToString() ?? "none");
        return environmentReference;
    }

    /// <summary>
    /// Issues a legacy unscoped reference for local command testing only.
    /// Such references cannot produce agent-addressed invalidation messages.
    /// </summary>
    public EnvironmentRef IssueSimpleReference(EntityUid entity, string? category = null)
    {
        var scope = new ReferenceScope
        {
            ConnectionId = ConnectionId.FromGuid(Guid.Empty),
            IssuedAtTick = new SimTick(0),
            ExpiresAtTick = null,
            BodyId = null,
            BodyGeneration = null,
        };
        return IssueReference(entity, scope, category);
    }

    /// <summary>
    /// Resolves a reference under complete connection/body/query authority.
    /// </summary>
    public EntityUid? TryResolve(
        EnvironmentRef environmentReference,
        EnvironmentReferenceResolutionContext context)
    {
        if (!_registry.TryResolve(environmentReference, context, out var entity))
        {
            _sawmill.Debug(
                "Reference {0} could not be resolved under the supplied authority",
                environmentReference);
            return null;
        }

        if (!_entityManager.EntityExists(entity))
        {
            _sawmill.Debug(
                "Reference {0} resolved to deleted entity {1}; invalidating locally",
                environmentReference,
                entity);
            InvalidateReference(environmentReference);
            return null;
        }

        return entity;
    }

    /// <summary>
    /// Legacy resolver retained until action targeting supplies complete authority context.
    /// </summary>
    public EntityUid? TryResolve(
        EnvironmentRef environmentReference,
        SimTick currentTick,
        uint? bodyGeneration = null)
    {
#pragma warning disable CS0618
        if (!_registry.TryResolve(
                environmentReference,
                currentTick,
                bodyGeneration,
                out var entity))
#pragma warning restore CS0618
        {
            _sawmill.Debug(
                "Reference {0} could not be resolved (invalid or expired)",
                environmentReference);
            return null;
        }

        if (!_entityManager.EntityExists(entity))
        {
            _sawmill.Debug(
                "Reference {0} resolved to deleted entity {1}; invalidating locally",
                environmentReference,
                entity);
            InvalidateReference(environmentReference);
            return null;
        }

        return entity;
    }

    public EntityUid? Resolve(EnvironmentRef environmentReference)
    {
        return TryResolve(environmentReference, new SimTick(0), null);
    }

    public void InvalidateReference(EnvironmentRef environmentReference)
    {
        _registry.InvalidateReference(environmentReference);
        lock (_metadataLock)
            _metadata.Remove(environmentReference);

        _sawmill.Debug("Invalidated reference {0}", environmentReference);
    }

    /// <summary>
    /// Invalidates all references pointing to one terminating Station entity.
    /// </summary>
    public IReadOnlyList<ReferenceInvalidationBatch> InvalidateForEntity(EntityUid entity)
    {
        return InvalidateWhere(metadata => metadata.Target == entity);
    }

    /// <summary>
    /// Invalidates references issued under one exact body-authority generation.
    /// </summary>
    public IReadOnlyList<ReferenceInvalidationBatch> InvalidateForBody(
        BodyId bodyId,
        uint bodyGeneration)
    {
        return InvalidateWhere(metadata =>
            metadata.Scope.BodyId == bodyId &&
            metadata.Scope.BodyGeneration == bodyGeneration);
    }

    /// <summary>
    /// Invalidates every reference owned by a connection.
    /// </summary>
    public IReadOnlyList<ReferenceInvalidationBatch> InvalidateForConnection(
        ConnectionId connectionId)
    {
        return InvalidateWhere(metadata => metadata.Scope.ConnectionId == connectionId);
    }

    /// <summary>
    /// Prunes stale core entries and matching adapter metadata.
    /// </summary>
    public int Prune(SimTick currentTick)
    {
        var removed = _registry.PruneStaleReferences(currentTick);
        lock (_metadataLock)
        {
            var stale = _metadata
                .Where(pair =>
                    pair.Value.Scope.ExpiresAtTick.HasValue &&
                    currentTick >= pair.Value.Scope.ExpiresAtTick.Value)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var reference in stale)
                _metadata.Remove(reference);
        }

        return removed;
    }

    public EnvironmentRef GetOrCreateReference(EntityUid entity, string? category = null)
    {
        return IssueSimpleReference(entity, category);
    }

    private IReadOnlyList<ReferenceInvalidationBatch> InvalidateWhere(
        Func<StationReferenceMetadata, bool> predicate)
    {
        List<StationReferenceMetadata> matches;
        lock (_metadataLock)
        {
            matches = _metadata.Values
                .Where(predicate)
                .OrderBy(metadata => metadata.Reference)
                .ToList();

            foreach (var metadata in matches)
                _metadata.Remove(metadata.Reference);
        }

        foreach (var metadata in matches)
            _registry.InvalidateReference(metadata.Reference);

        if (matches.Count == 0)
            return Array.Empty<ReferenceInvalidationBatch>();

        _sawmill.Debug("Invalidated {0} scoped environment references", matches.Count);

        return matches
            .Where(metadata => metadata.AgentId.HasValue)
            .GroupBy(metadata => new
            {
                AgentId = metadata.AgentId!.Value,
                metadata.Scope.ConnectionId,
            })
            .OrderBy(group => group.Key.ConnectionId.ToString(), StringComparer.Ordinal)
            .ThenBy(group => group.Key.AgentId.ToString(), StringComparer.Ordinal)
            .Select(group => new ReferenceInvalidationBatch(
                group.Key.AgentId,
                group.Key.ConnectionId,
                group.Select(metadata => metadata.Reference).ToList()))
            .ToList();
    }

    private sealed record StationReferenceMetadata(
        EnvironmentRef Reference,
        EntityUid Target,
        ReferenceScope Scope,
        AgentId? AgentId,
        string? Category);
}

/// <summary>
/// Agent-addressed invalidation batch retained entirely in the adapter until it is mapped
/// to the canonical COGR invalidation message.
/// </summary>
public sealed record ReferenceInvalidationBatch(
    AgentId AgentId,
    ConnectionId ConnectionId,
    IReadOnlyList<EnvironmentRef> References);
