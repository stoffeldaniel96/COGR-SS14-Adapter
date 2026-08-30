using System;
using System.Collections.Generic;
using COGR.Contracts.Embodiment;
using COGR.Contracts.Messages;
using COGR.Core.Identifiers;
using COGR.Core.Sequences;
using COGR.Core.Time;
using COGR.Transport.Grpc.Mapping;
using Content.Server.COGR;
using Content.Shared.COGR.Components;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Publishes sparse, bounded proprioceptive evidence for COGR-controlled bodies.
/// Native nutrition values remain Station-local; the numeric channel is only a bounded sensory
/// measurement and does not encode intrinsic COGR meaning or expose SS14's raw physiology scale.
/// </summary>
/// <remarks>
/// Passive observation is event-driven. Robust's public <see cref="IEntityManager.EntityDirtied"/>
/// notification wakes this system only for COGR-controlled bodies whose native networked state changed.
/// Reads are deferred/coalesced by one tick so multiple native mutations settle before observation.
/// Controlled-body startup/shutdown remain exclusively owned by <see cref="COGRBodyAuthorityCoordinatorSystem"/>.
/// </remarks>
public sealed partial class COGRBodySensoryEvidenceSystem : EntitySystem
{
    private const uint ModerateFloor = 250_000;
    private const uint SevereFloor = 700_000;
    private const uint ModerateSpan = SevereFloor - ModerateFloor;
    private const uint SevereSpan = BodySensoryIntensity.One - SevereFloor;

    private static readonly BodySensoryChannelKey NourishmentChannel =
        new("body.nourishment-pressure.v1");
    private static readonly BodySensoryChannelKey HydrationChannel =
        new("body.hydration-pressure.v1");

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private HungerSystem _hunger = default!;

    private readonly Dictionary<AuthorityKey, BodySensoryEvidenceSequence> _sequences = new();
    private readonly Dictionary<EntityUid, HungerThreshold> _observedHunger = new();
    private readonly Dictionary<EntityUid, ThirstThreshold> _observedThirst = new();
    private readonly Dictionary<EntityUid, PendingDirtyBody> _dirtyBodies = new();
    private readonly List<EntityUid> _readyBodies = new();
    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _sawmill = _logManager.GetSawmill("cogr.body-sensory");

        EntityManager.EntityDirtied += OnEntityDirtied;
    }

    public override void Shutdown()
    {
        EntityManager.EntityDirtied -= OnEntityDirtied;
        _dirtyBodies.Clear();
        _readyBodies.Clear();
        _observedHunger.Clear();
        _observedThirst.Clear();
        _sequences.Clear();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_dirtyBodies.Count == 0)
            return;

        var currentTick = (ulong)_timing.CurTick.Value;
        _readyBodies.Clear();
        foreach (var (uid, pending) in _dirtyBodies)
        {
            if (currentTick > pending.FirstDirtyTick)
                _readyBodies.Add(uid);
        }

        foreach (var uid in _readyBodies)
        {
            if (!_dirtyBodies.TryGetValue(uid, out var pending))
                continue;

            if (TryComp<COGRControlledComponent>(uid, out var controlled) && controlled.IsActive)
            {
                ObservePassiveHunger(uid, controlled);
                ObservePassiveThirst(uid, controlled);
            }

            // If the entity dirtied again after this batch first became pending, retain one
            // additional deferred observation. This prevents same-tick coalescing in Robust's
            // EntityDirtied event from hiding a later native body mutation.
            if (_dirtyBodies.TryGetValue(uid, out var latest) &&
                latest.LatestDirtyTick > pending.FirstDirtyTick)
            {
                _dirtyBodies[uid] = new PendingDirtyBody(
                    latest.LatestDirtyTick,
                    latest.LatestDirtyTick);
            }
            else
            {
                _dirtyBodies.Remove(uid);
            }
        }
    }

    private void OnEntityDirtied(Entity<MetaDataComponent> entity)
    {
        if (!TryComp<COGRControlledComponent>(entity.Owner, out var controlled) || !controlled.IsActive)
            return;

        QueueDirtyBody(entity.Owner);
    }

    private void QueueDirtyBody(EntityUid uid)
    {
        var tick = (ulong)_timing.CurTick.Value;
        if (_dirtyBodies.TryGetValue(uid, out var pending))
        {
            _dirtyBodies[uid] = pending with { LatestDirtyTick = tick };
            return;
        }

        _dirtyBodies.Add(uid, new PendingDirtyBody(tick, tick));
    }

    private void ObservePassiveHunger(EntityUid uid, COGRControlledComponent controlled)
    {
        if (!TryComp<HungerComponent>(uid, out var hunger))
        {
            _observedHunger.Remove(uid);
            return;
        }

        var current = hunger.CurrentThreshold;
        if (_observedHunger.TryGetValue(uid, out var previous) && previous == current)
            return;

        if (PublishCurrentHunger(uid, controlled, BodySensoryEvidenceAcquisition.PassiveEvent))
            _observedHunger[uid] = current;
    }

    private void ObservePassiveThirst(EntityUid uid, COGRControlledComponent controlled)
    {
        if (!TryComp<ThirstComponent>(uid, out var thirst))
        {
            _observedThirst.Remove(uid);
            return;
        }

        var current = thirst.CurrentThirstThreshold;
        if (_observedThirst.TryGetValue(uid, out var previous) && previous == current)
            return;

        if (PublishCurrentThirst(uid, controlled, BodySensoryEvidenceAcquisition.PassiveEvent))
            _observedThirst[uid] = current;
    }

    /// <summary>
    /// Publishes a fresh bounded nourishment observation for one exact controlled body.
    /// Calling this method is deliberately explicit; future cognitive introspection may invoke it
    /// only after paying the ordinary bounded perceptual/procedural cost.
    /// </summary>
    public bool PublishCurrentHunger(
        EntityUid uid,
        BodySensoryEvidenceAcquisition acquisition = BodySensoryEvidenceAcquisition.ActiveInspection)
    {
        if (!TryComp<COGRControlledComponent>(uid, out var controlled) ||
            !TryComp<HungerComponent>(uid, out var hunger))
        {
            return false;
        }

        var published = PublishCurrentHunger(uid, controlled, acquisition);
        if (published)
            _observedHunger[uid] = hunger.CurrentThreshold;
        return published;
    }

    /// <summary>
    /// Publishes a fresh bounded hydration observation for one exact controlled body.
    /// </summary>
    public bool PublishCurrentThirst(
        EntityUid uid,
        BodySensoryEvidenceAcquisition acquisition = BodySensoryEvidenceAcquisition.ActiveInspection)
    {
        if (!TryComp<COGRControlledComponent>(uid, out var controlled) ||
            !TryComp<ThirstComponent>(uid, out var thirst))
        {
            return false;
        }

        var published = PublishCurrentThirst(uid, controlled, acquisition);
        if (published)
            _observedThirst[uid] = thirst.CurrentThirstThreshold;
        return published;
    }

    /// <summary>Clears body sensory bookkeeping when one controlled embodiment is removed.</summary>
    public void NotifyControlledBodyRemoved(EntityUid uid, COGRControlledComponent controlled)
    {
        _dirtyBodies.Remove(uid);
        _observedHunger.Remove(uid);
        _observedThirst.Remove(uid);

        if (controlled.AgentId == Guid.Empty || controlled.BodyId == Guid.Empty)
            return;

        var agentId = AgentId.FromGuid(controlled.AgentId);
        var bodyId = BodyId.FromGuid(controlled.BodyId);
        var stale = new List<AuthorityKey>();
        foreach (var key in _sequences.Keys)
        {
            if (key.AgentId == agentId && key.BodyId == bodyId)
                stale.Add(key);
        }

        foreach (var key in stale)
            _sequences.Remove(key);
    }

    private bool PublishCurrentHunger(
        EntityUid uid,
        COGRControlledComponent controlled,
        BodySensoryEvidenceAcquisition acquisition)
    {
        if (!TryComp<HungerComponent>(uid, out var hunger))
            return false;

        var intensity = ResolveHungerIntensity(hunger);
        return Publish(
            controlled,
            NourishmentChannel,
            HungerStateKey(hunger.CurrentThreshold),
            intensity,
            acquisition);
    }

    private bool PublishCurrentThirst(
        EntityUid uid,
        COGRControlledComponent controlled,
        BodySensoryEvidenceAcquisition acquisition)
    {
        if (!TryComp<ThirstComponent>(uid, out var thirst))
            return false;

        var intensity = ResolveThirstIntensity(thirst);
        return Publish(
            controlled,
            HydrationChannel,
            ThirstStateKey(thirst.CurrentThirstThreshold),
            intensity,
            acquisition);
    }

    private bool Publish(
        COGRControlledComponent controlled,
        BodySensoryChannelKey channel,
        BodySensoryStateKey state,
        BodySensoryIntensity intensity,
        BodySensoryEvidenceAcquisition acquisition)
    {
        if (!controlled.IsActive
            || controlled.AgentId == Guid.Empty
            || controlled.BodyId == Guid.Empty
            || _adapter.Connection is not { IsConnected: true } connection
            || connection.ConnectionId == Guid.Empty
            || !_authority.BoundWorld.HasValue
            || !_authority.BoundConnection.HasValue)
        {
            return false;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        if (_authority.BoundConnection.Value != connectionId)
            return false;

        var agentId = AgentId.FromGuid(controlled.AgentId);
        var bodyId = BodyId.FromGuid(controlled.BodyId);
        var lease = _authority.ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue
            || lease.Value.BodyId != bodyId
            || !_authority.ResolveBoundBody(agentId, bodyId, connectionId, lease.Value.Generation).HasValue)
        {
            return false;
        }

        var key = new AuthorityKey(connectionId, agentId, bodyId, lease.Value.Generation);
        var sequence = _sequences.TryGetValue(key, out var previous)
            ? previous.Next()
            : BodySensoryEvidenceSequence.First;
        _sequences[key] = sequence;

        var evidence = new BodySensoryEvidenceEvent
        {
            Scope = new EmbodimentSupportAuthorityScope
            {
                ConnectionId = connectionId,
                AgentId = agentId,
                BodyId = bodyId,
                BodyGeneration = lease.Value.Generation,
            },
            Sequence = sequence,
            Channel = channel,
            State = state,
            Intensity = intensity,
            Acquisition = acquisition,
        };

        connection.EnqueueEnvironmentMessage(new PerceptionMessage
        {
            WorldId = _authority.BoundWorld.Value,
            ConnectionId = connectionId,
            Tick = new SimTick((ulong)_timing.CurTick.Value),
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            AgentId = agentId,
            PerceptId = PerceptId.NewId(),
            Category = PerceptionCategory.Proprioceptive,
            Data = BodySensoryEvidenceWireCodec.Encode(evidence),
            Format = BodySensoryEvidenceWireCodec.EventFormat,
        });

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[AUTO] body-sensory.publish agent={0} body={1} generation={2} channel={3} state={4} intensity={5} saturated={6} acquisition={7}",
                agentId,
                bodyId,
                lease.Value.Generation,
                channel,
                state,
                intensity.Units,
                intensity.IsSaturated,
                acquisition);
        }

        return true;
    }

    private BodySensoryIntensity ResolveHungerIntensity(HungerComponent component)
    {
        var current = _hunger.GetHunger(component);
        return component.CurrentThreshold switch
        {
            HungerThreshold.Overfed or HungerThreshold.Okay => BodySensoryIntensity.Zero,
            HungerThreshold.Peckish => BoundedWithinBand(
                current,
                component.Thresholds[HungerThreshold.Peckish],
                component.Thresholds[HungerThreshold.Starving],
                ModerateFloor,
                ModerateSpan),
            HungerThreshold.Starving => BoundedWithinBand(
                current,
                component.Thresholds[HungerThreshold.Starving],
                component.Thresholds[HungerThreshold.Dead],
                SevereFloor,
                SevereSpan),
            HungerThreshold.Dead => new BodySensoryIntensity(BodySensoryIntensity.One, saturated: true),
            _ => BodySensoryIntensity.Zero,
        };
    }

    private static BodySensoryIntensity ResolveThirstIntensity(ThirstComponent component)
    {
        var current = component.CurrentThirst;
        return component.CurrentThirstThreshold switch
        {
            ThirstThreshold.OverHydrated or ThirstThreshold.Okay => BodySensoryIntensity.Zero,
            ThirstThreshold.Thirsty => BoundedWithinBand(
                current,
                component.ThirstThresholds[ThirstThreshold.Thirsty],
                component.ThirstThresholds[ThirstThreshold.Parched],
                ModerateFloor,
                ModerateSpan),
            ThirstThreshold.Parched => BoundedWithinBand(
                current,
                component.ThirstThresholds[ThirstThreshold.Parched],
                component.ThirstThresholds[ThirstThreshold.Dead],
                SevereFloor,
                SevereSpan),
            ThirstThreshold.Dead => new BodySensoryIntensity(BodySensoryIntensity.One, saturated: true),
            _ => BodySensoryIntensity.Zero,
        };
    }

    private static BodySensoryIntensity BoundedWithinBand(
        float current,
        float bandStart,
        float bandEnd,
        uint outputStart,
        uint outputSpan)
    {
        var denominator = bandStart - bandEnd;
        var progress = denominator <= 0f
            ? 0f
            : Math.Clamp((bandStart - current) / denominator, 0f, 1f);
        var units = outputStart + (uint)Math.Round(outputSpan * progress, MidpointRounding.AwayFromZero);
        if (units >= BodySensoryIntensity.One)
            return new BodySensoryIntensity(BodySensoryIntensity.One, saturated: false);
        return new BodySensoryIntensity(units, saturated: false);
    }

    private static BodySensoryStateKey HungerStateKey(HungerThreshold threshold) => threshold switch
    {
        HungerThreshold.Overfed => new BodySensoryStateKey("overfed"),
        HungerThreshold.Okay => new BodySensoryStateKey("okay"),
        HungerThreshold.Peckish => new BodySensoryStateKey("peckish"),
        HungerThreshold.Starving => new BodySensoryStateKey("starving"),
        HungerThreshold.Dead => new BodySensoryStateKey("dead"),
        _ => new BodySensoryStateKey("unknown"),
    };

    private static BodySensoryStateKey ThirstStateKey(ThirstThreshold threshold) => threshold switch
    {
        ThirstThreshold.OverHydrated => new BodySensoryStateKey("overhydrated"),
        ThirstThreshold.Okay => new BodySensoryStateKey("okay"),
        ThirstThreshold.Thirsty => new BodySensoryStateKey("thirsty"),
        ThirstThreshold.Parched => new BodySensoryStateKey("parched"),
        ThirstThreshold.Dead => new BodySensoryStateKey("dead"),
        _ => new BodySensoryStateKey("unknown"),
    };

    private readonly record struct PendingDirtyBody(
        ulong FirstDirtyTick,
        ulong LatestDirtyTick);

    private readonly record struct AuthorityKey(
        ConnectionId ConnectionId,
        AgentId AgentId,
        BodyId BodyId,
        uint Generation);
}
