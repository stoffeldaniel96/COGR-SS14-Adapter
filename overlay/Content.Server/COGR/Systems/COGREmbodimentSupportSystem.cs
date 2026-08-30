using System;
using System.Collections.Generic;
using COGR.Contracts.Embodiment;
using COGR.Contracts.Messages;
using COGR.Core.Identifiers;
using COGR.Core.Sequences;
using COGR.Core.Time;
using COGR.Transport.Grpc.Mapping;
using Content.Server.COGR;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Publishes a bounded normalized objective body-support channel for active COGR embodiments.
/// Native MobState is consumed only inside Station and is never transmitted as a cognitive label or corpus identity.
/// Support publication is event-driven: authority establishment and MobState changes are the only wake sources.
/// </summary>
public sealed partial class COGREmbodimentSupportSystem : EntitySystem
{
    private const uint CriticalOperationalSupport = 500_000;

    private static readonly EmbodimentSupportChannelKey OperationalSupportChannel =
        new("ss14.body.operational-support.v1");

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
        _sawmill = _logManager.GetSawmill("cogr.embodiment");
        SubscribeEmbodimentEvents();
    }

    private EmbodimentSupportChannelValue ResolveOperationalSupport(EntityUid body)
    {
        if (!TryComp<MobStateComponent>(body, out var mobState))
            return EmbodimentSupportChannelValue.Zero;

        return mobState.CurrentState switch
        {
            MobState.Alive => EmbodimentSupportChannelValue.Full,
            MobState.Critical => new EmbodimentSupportChannelValue(CriticalOperationalSupport),
            MobState.Dead or MobState.Invalid => EmbodimentSupportChannelValue.Zero,
            _ => EmbodimentSupportChannelValue.Zero,
        };
    }

    private bool PublishIfNeeded(
        COGRConnectionManager connection,
        WorldId worldId,
        EmbodimentSupportAuthorityScope scope,
        EmbodimentSupportChannelValue support,
        SimTick tick)
    {
        var authorityChanged = !_published.TryGetValue(scope.AgentId, out var state)
            || state.ConnectionId != scope.ConnectionId
            || state.BodyId != scope.BodyId
            || state.BodyGeneration != scope.BodyGeneration;
        var supportChanged = authorityChanged || state!.Support != support;
        if (!supportChanged)
            return false;

        var sequence = authorityChanged
            ? EmbodimentSupportSnapshotSequence.First
            : state!.Sequence.Next();
        var snapshot = new EmbodimentSupportSnapshot(
            scope,
            sequence,
            [new EmbodimentSupportChannelSample(OperationalSupportChannel, support)]);

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
            Data = EmbodimentSupportWireCodec.EncodeSnapshot(snapshot),
            Format = EmbodimentSupportWireCodec.SnapshotFormat,
        });

        _published[scope.AgentId] = new PublishedState(
            scope.ConnectionId,
            scope.BodyId,
            scope.BodyGeneration,
            sequence,
            support);
        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[AUTO] embodiment.publish agent={0} generation={1} sequence={2} support={3} reason={4}",
                scope.AgentId,
                scope.BodyGeneration,
                sequence,
                support.Units,
                authorityChanged ? "authority" : "support_change");
        }

        return true;
    }

    private sealed record PublishedState(
        ConnectionId ConnectionId,
        BodyId BodyId,
        uint BodyGeneration,
        EmbodimentSupportSnapshotSequence Sequence,
        EmbodimentSupportChannelValue Support);
}
