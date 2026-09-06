using COGR.Contracts.Embodiment;
using COGR.Contracts.Messages;
using COGR.Core.Actions;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using COGR.Transport.Grpc.Mapping;
using Content.Server.COGR;
using Content.Server.NPC.Components;
using Content.Shared.COGR.Components;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Publishes bounded normalized evidence about the useful control resolution of projected locomotion.
/// Station-native steering range and calibration remain adapter truth; COGR receives only the equivalent
/// normalized local displacement and interprets it through the Coggent's fallible body schema.
/// </summary>
public sealed class COGRLocomotorRealizabilitySystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _logManager = default!;

    private readonly Dictionary<AgentId, PublishedState> _published = new();
    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _sawmill = _logManager.GetSawmill("cogr.embodiment.locomotion");
    }

    /// <summary>
    /// Publishes the initial generic-humanoid projected-locomotion resolution after exact body authority is established.
    /// An already-present native steering component is authoritative for its configured range; otherwise the Station
    /// component default is used as provisional embodiment evidence until a live steering registration is observed.
    /// </summary>
    public void NotifyControlledBodyAuthorityBound(EntityUid uid, COGRControlledComponent controlled)
    {
        var nativeRange = TryComp<NPCSteeringComponent>(uid, out var existing)
            ? existing.Range
            : new NPCSteeringComponent().Range;
        PublishCurrent(controlled, nativeRange, "authority");
    }

    /// <summary>
    /// Refines current evidence from the exact native steering component registered for a projected objective.
    /// This is still evidence rather than cognitive truth; a changed range emits a fresh sequence.
    /// </summary>
    public void ObserveProjectedSteeringRange(ActionAttempt attempt, float nativeRange)
    {
        if (!attempt.AuthorityLease.IsValid
            || attempt.AuthorityLease.AgentId != attempt.AgentId
            || attempt.AuthorityLease.BodyId != attempt.BodyId)
        {
            return;
        }

        var connection = _adapter.Connection;
        var boundWorld = _authority.BoundWorld;
        var boundConnection = _authority.BoundConnection;
        if (connection is not { IsConnected: true }
            || connection.ConnectionId == Guid.Empty
            || !boundWorld.HasValue
            || !boundConnection.HasValue)
        {
            return;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        if (boundConnection.Value != connectionId
            || attempt.AuthorityLease.ConnectionId != connectionId
            || !_authority.ResolveBoundBody(
                    attempt.AgentId,
                    attempt.BodyId,
                    connectionId,
                    attempt.AuthorityLease.Generation).HasValue)
        {
            return;
        }

        PublishIfNeeded(
            connection,
            boundWorld.Value,
            new EmbodimentLocomotorRealizabilityAuthorityScope
            {
                ConnectionId = connectionId,
                AgentId = attempt.AgentId,
                BodyId = attempt.BodyId,
                BodyGeneration = attempt.AuthorityLease.Generation,
            },
            nativeRange,
            new SimTick((ulong)_timing.CurTick.Value),
            "live_steering");
    }

    /// <summary>Forgets adapter publication state when the controlled body leaves the adapter.</summary>
    public void NotifyControlledBodyRemoved(COGRControlledComponent controlled)
    {
        if (controlled.AgentId == Guid.Empty)
            return;

        _published.Remove(AgentId.FromGuid(controlled.AgentId));
    }

    private void PublishCurrent(
        COGRControlledComponent controlled,
        float nativeRange,
        string reason)
    {
        var connection = _adapter.Connection;
        var boundWorld = _authority.BoundWorld;
        var boundConnection = _authority.BoundConnection;
        if (connection is not { IsConnected: true }
            || connection.ConnectionId == Guid.Empty
            || !boundWorld.HasValue
            || !boundConnection.HasValue
            || controlled.AgentId == Guid.Empty
            || controlled.BodyId == Guid.Empty
            || !controlled.IsActive)
        {
            return;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        if (boundConnection.Value != connectionId)
            return;

        var agentId = AgentId.FromGuid(controlled.AgentId);
        var bodyId = BodyId.FromGuid(controlled.BodyId);
        var lease = _authority.ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue
            || lease.Value.BodyId != bodyId
            || !_authority.ResolveBoundBody(agentId, bodyId, connectionId, lease.Value.Generation).HasValue)
        {
            return;
        }

        PublishIfNeeded(
            connection,
            boundWorld.Value,
            new EmbodimentLocomotorRealizabilityAuthorityScope
            {
                ConnectionId = connectionId,
                AgentId = agentId,
                BodyId = bodyId,
                BodyGeneration = lease.Value.Generation,
            },
            nativeRange,
            new SimTick((ulong)_timing.CurTick.Value),
            reason);
    }

    private bool PublishIfNeeded(
        COGRConnectionManager connection,
        WorldId worldId,
        EmbodimentLocomotorRealizabilityAuthorityScope scope,
        float nativeRange,
        SimTick tick,
        string reason)
    {
        if (!TryResolveMinimumReliableProjectedDisplacementLocalUnits(nativeRange, out var localMinimum))
        {
            _sawmill.Warning(
                "Cannot publish projected-locomotion realizability for agent {0}: native steering range {1} is invalid",
                scope.AgentId,
                nativeRange);
            return false;
        }

        var authorityChanged = !_published.TryGetValue(scope.AgentId, out var state)
            || state.ConnectionId != scope.ConnectionId
            || state.BodyId != scope.BodyId
            || state.BodyGeneration != scope.BodyGeneration;
        var resolutionChanged = authorityChanged || state!.MinimumReliableProjectedDisplacementLocalUnits != localMinimum;
        if (!resolutionChanged)
            return false;

        var sequence = authorityChanged
            ? EmbodimentLocomotorRealizabilitySnapshotSequence.First
            : state!.Sequence.Next();
        var snapshot = new EmbodimentLocomotorRealizabilitySnapshot(
            scope,
            sequence,
            localMinimum);

        connection.EnqueueEnvironmentMessage(new PerceptionMessage
        {
            WorldId = worldId,
            ConnectionId = scope.ConnectionId,
            Tick = tick,
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            AgentId = scope.AgentId,
            PerceptId = PerceptId.NewId(),
            Category = PerceptionCategory.Proprioceptive,
            Data = EmbodimentLocomotorRealizabilityWireCodec.EncodeSnapshot(snapshot),
            Format = EmbodimentLocomotorRealizabilityWireCodec.SnapshotFormat,
        });

        _published[scope.AgentId] = new PublishedState(
            scope.ConnectionId,
            scope.BodyId,
            scope.BodyGeneration,
            sequence,
            localMinimum);

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[AUTO] locomotor-realizability.publish agent={0} generation={1} sequence={2} minimumLocal={3:F4} reason={4}",
                scope.AgentId,
                scope.BodyGeneration,
                sequence,
                localMinimum,
                reason);
        }

        return true;
    }

    private static bool TryResolveMinimumReliableProjectedDisplacementLocalUnits(
        float nativeSteeringRange,
        out double localUnits)
    {
        localUnits = 0.0d;
        if (!float.IsFinite(nativeSteeringRange) || nativeSteeringRange <= 0f)
            return false;

        try
        {
            localUnits = COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                nativeSteeringRange);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return double.IsFinite(localUnits) && localUnits > 0.0d;
    }

    private sealed record PublishedState(
        ConnectionId ConnectionId,
        BodyId BodyId,
        uint BodyGeneration,
        EmbodimentLocomotorRealizabilitySnapshotSequence Sequence,
        double MinimumReliableProjectedDisplacementLocalUnits);
}
