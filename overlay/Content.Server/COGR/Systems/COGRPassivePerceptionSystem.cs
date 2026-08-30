using System;
using System.Collections.Generic;
using System.Linq;
using COGR.Contracts.Messages;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using COGR.Transport.Grpc.Mapping;
using Content.Shared.ActionBlocker;
using Content.Shared.COGR.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Systems;
using Content.Shared.Speech;
using Robust.Shared.Log;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRPassivePerceptionSystem : EntitySystem
{
    private const int MaximumUtteranceLength = 4096;
    private const string GenericHumanoidEmbodimentProfile =
        "ss14.generic-humanoid.v1";
    private const string LosBinaryOcclusionModel = "los_binary_proxy";

    private static readonly TimeSpan BodyCueQuietPeriod =
        TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan BodyCueMaximumDelay =
        TimeSpan.FromSeconds(5);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private MobStateSystem _mobState = default!;

    private readonly Dictionary<BodyCueKey, PendingBodyCue> _pendingBodyCues = new();
    private readonly List<BodyCueKey> _readyBodyCues = new();

    private COGRAdapterSystem _adapter = default!;
    private COGRBodyAuthorityCoordinatorSystem _authority = default!;
    private COGRBoundedPerceptionSystem _boundedPerception = default!;
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();
        _adapter = EntityManager.System<COGRAdapterSystem>();
        _authority = EntityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        _boundedPerception = EntityManager.System<COGRBoundedPerceptionSystem>();
        _sawmill = _logManager.GetSawmill("cogr.passive-perception");

        SubscribeLocalEvent<COGRControlledComponent, ListenEvent>(OnListen);
#pragma warning disable CS0618 // Intentional: this is the only current post-clamp damage delta event in SS14.
        SubscribeLocalEvent<COGRControlledComponent, DamageChangedEvent>(OnDamageChanged);
#pragma warning restore CS0618
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pendingBodyCues.Count == 0)
            return;

        var now = _timing.CurTime;
        _readyBodyCues.Clear();

        foreach (var (key, pending) in _pendingBodyCues)
        {
            var quietPeriodElapsed =
                now - pending.LastObservedAt >= BodyCueQuietPeriod;
            var maximumDelayElapsed =
                now - pending.FirstObservedAt >= BodyCueMaximumDelay;
            if (quietPeriodElapsed || maximumDelayElapsed)
                _readyBodyCues.Add(key);
        }

        foreach (var key in _readyBodyCues)
        {
            _pendingBodyCues.Remove(key);

            if (!TryComp<COGRControlledComponent>(key.Entity, out var component) ||
                !TryGetContext((key.Entity, component), out var context))
            {
                continue;
            }

            PublishBodyCue(context, key.Kind, interruptive: false);
        }
    }

    private void OnListen(
        Entity<COGRControlledComponent> entity,
        ref ListenEvent args)
    {
        if (!_actionBlocker.CanConsciouslyPerformAction(entity.Owner))
            return;

        var utterance = args.Message.Trim();
        if (string.IsNullOrWhiteSpace(utterance))
            return;

        var payloadIntegrity = args.PayloadIntegrity;
        var payloadMutated = args.PayloadMutated;
        var mutationKinds = new List<string>();
        if (args.PayloadMutated)
            mutationKinds.Add("whisper_obfuscation");

        if (utterance.Length > MaximumUtteranceLength)
        {
            payloadIntegrity *=
                (double)MaximumUtteranceLength / utterance.Length;
            utterance = utterance[..MaximumUtteranceLength];
            payloadMutated = true;
            mutationKinds.Add("adapter_text_truncation");
        }

        if (!TryGetContext(entity, out var context))
            return;

        var registry = _adapter.ReferenceRegistry;
        if (registry == null || Deleted(args.Source))
            return;

        var isWhisper = args.DeliveryMode != SpeechDeliveryMode.Talk;
        var isMuffled = args.DeliveryMode == SpeechDeliveryMode.WhisperMuffled;
        var isSelfSource = args.Source == entity.Owner;
        var lineOfSight = _examine.InRangeUnOccluded(
            entity.Owner,
            args.Source,
            0f);
        var sourceIdentifiable = isSelfSource || !isMuffled || lineOfSight;
        var sourceResolution = isSelfSource
            ? CueSourceResolution.Self
            : !sourceIdentifiable
                ? CueSourceResolution.Anonymous
                : lineOfSight
                    ? CueSourceResolution.Direct
                    : CueSourceResolution.Attributed;
        var directionResolution = isSelfSource
            ? CueDirectionResolution.Self
            : lineOfSight
                ? CueDirectionResolution.Exact
                : CueDirectionResolution.Unknown;

        EnvironmentRef? sourceReference = null;
        if (sourceIdentifiable && !isSelfSource)
        {
            sourceReference =
                _boundedPerception.GetOrCreateReferenceForObservedEntity(
                    args.Source,
                    context.ConnectionId,
                    context.AgentId,
                    context.BodyId,
                    context.BodyGeneration,
                    context.Tick,
                    "actor",
                    registry);
        }

        var eventClass = args.DeliveryMode switch
        {
            SpeechDeliveryMode.WhisperClear => "local_whisper_clear_received",
            SpeechDeliveryMode.WhisperMuffled => "local_whisper_muffled_received",
            _ => "local_talk_received",
        };

        Publish(
            context,
            PerceptionCategory.Auditory,
            new PerceptualEvent
            {
                EvidenceId = Guid.CreateVersion7(),
                ObservedAtTick = context.Tick,
                AgentId = context.AgentId,
                BodyId = context.BodyId,
                CausalTraceId = CausalTraceId.FromGuid(Guid.CreateVersion7()),
                SourceQueryId = null,
                ObservationType = ObservationType.Communication,
                UrgencyHint = isWhisper ? 35 : 45,
                EventClass = eventClass,
                Features =
                [
                    new ObservedFeature
                    {
                        Category = "communication",
                        FeatureType = "utterance",
                        Value = utterance,
                        Confidence = 1.0,
                    },
                    new ObservedFeature
                    {
                        Category = "communication",
                        FeatureType = "speech_mode",
                        Value = isWhisper ? "whisper" : "talk",
                        Confidence = 1.0,
                    },
                    new ObservedFeature
                    {
                        Category = "communication",
                        FeatureType = "source_activity",
                        Value = "speaking",
                        Confidence = 1.0,
                    },
                    new ObservedFeature
                    {
                        Category = "relation",
                        FeatureType = "source_reference_scope",
                        Value = sourceReference.HasValue ? "passive_only" : "none",
                        Confidence = 1.0,
                    },
                ],
                Transmission = new CueTransmission
                {
                    EmbodimentProfile = GenericHumanoidEmbodimentProfile,
                    Modality = CueModality.Auditory,
                    Channel = "local_speech",
                    RangeFraction = args.RangeFraction,
                    LineOfSight = lineOfSight,
                    Occlusion = lineOfSight ? 0.0 : 1.0,
                    OcclusionModel = LosBinaryOcclusionModel,
                    Attenuation = null,
                    PayloadIntegrity = Math.Clamp(payloadIntegrity, 0.0, 1.0),
                    PayloadMutated = payloadMutated,
                    MutationKinds = mutationKinds,
                    SourceResolution = sourceResolution,
                    DirectionResolution = directionResolution,
                },
                SourceRef = sourceReference,
                SpatialRelation = isWhisper
                    ? "within_whisper_range"
                    : "within_voice_range",
                Salience = isWhisper ? 0.65 : 0.75,
            });
    }

#pragma warning disable CS0618 // Preserve actual post-clamp disturbance/recovery semantics until SS14 replaces this event.
    private void OnDamageChanged(
        EntityUid uid,
        COGRControlledComponent component,
        DamageChangedEvent args)
    {
        // DamageDealtEvent represents an attempted delta before InjurableComponent clamps
        // healing at zero. DamageChangedEvent carries the post-clamp delta that actually
        // changed the body's damage state. Ignore direct sets and empty/no-op changes.
        var damageDelta = args.DamageDelta;
        if (damageDelta == null || damageDelta.Empty)
            return;

        var hasPositiveDamage = damageDelta.DamageDict.Values.Any(
            value => value > FixedPoint2.Zero);
        var hasHealing = damageDelta.DamageDict.Values.Any(
            value => value < FixedPoint2.Zero);
        if (!hasPositiveDamage && !hasHealing)
            return;

        if (!TryGetContext((uid, component), out var context))
            return;

        // Preserve immediate interruptive evidence. Non-interruptive damage and recovery
        // ticks are body-internal streams, so consolidate them after a quiet period while
        // enforcing a maximum delay for streams that never become quiet.
        if (hasPositiveDamage)
        {
            if (args.InterruptsDoAfters)
            {
                _pendingBodyCues.Remove(
                    new BodyCueKey(uid, BodyCueKind.Disturbance));
                PublishBodyCue(
                    context,
                    BodyCueKind.Disturbance,
                    interruptive: true);
            }
            else
            {
                QueueBodyCue(uid, BodyCueKind.Disturbance);
            }

            return;
        }

        QueueBodyCue(uid, BodyCueKind.Recovery);
    }
#pragma warning restore CS0618

    private void QueueBodyCue(EntityUid uid, BodyCueKind kind)
    {
        var key = new BodyCueKey(uid, kind);
        var now = _timing.CurTime;

        if (_pendingBodyCues.TryGetValue(key, out var pending))
        {
            pending.LastObservedAt = now;
            return;
        }

        _pendingBodyCues.Add(key, new PendingBodyCue(now));
    }

    private void PublishBodyCue(
        PassiveContext context,
        BodyCueKind kind,
        bool interruptive)
    {
        var isDisturbance = kind == BodyCueKind.Disturbance;
        var features = new List<ObservedFeature>
        {
            new()
            {
                Category = "state",
                FeatureType = isDisturbance
                    ? "bodily_disturbance"
                    : "bodily_recovery",
                Value = true,
                Confidence = isDisturbance ? 0.95 : 0.9,
            },
        };

        if (interruptive)
        {
            features.Add(new ObservedFeature
            {
                Category = "state",
                FeatureType = "activity_disrupted",
                Value = true,
                Confidence = 0.9,
            });
        }

        Publish(
            context,
            PerceptionCategory.Proprioceptive,
            new PerceptualEvent
            {
                EvidenceId = Guid.CreateVersion7(),
                ObservedAtTick = context.Tick,
                AgentId = context.AgentId,
                BodyId = context.BodyId,
                CausalTraceId = CausalTraceId.FromGuid(Guid.CreateVersion7()),
                SourceQueryId = null,
                ObservationType = interruptive
                    ? ObservationType.Interruptive
                    : ObservationType.Proprioceptive,
                UrgencyHint = isDisturbance
                    ? interruptive ? 95 : 75
                    : 35,
                EventClass = isDisturbance
                    ? "bodily_disturbance"
                    : "bodily_recovery",
                Features = features,
                Transmission = CreateBodyInternalTransmission(),
                SourceRef = null,
                SpatialRelation = null,
                Salience = isDisturbance
                    ? interruptive ? 1.0 : 0.9
                    : 0.55,
            });
    }

    private static CueTransmission CreateBodyInternalTransmission() => new()
    {
        EmbodimentProfile = GenericHumanoidEmbodimentProfile,
        Modality = CueModality.Proprioceptive,
        Channel = "body_internal",
        RangeFraction = null,
        LineOfSight = null,
        Occlusion = null,
        OcclusionModel = null,
        Attenuation = null,
        PayloadIntegrity = 1.0,
        PayloadMutated = false,
        MutationKinds = Array.Empty<string>(),
        SourceResolution = CueSourceResolution.Self,
        DirectionResolution = CueDirectionResolution.Self,
    };

    private bool TryGetContext(
        Entity<COGRControlledComponent> entity,
        out PassiveContext context)
    {
        context = default;
        if (!entity.Comp.IsActive ||
            entity.Comp.AgentId == Guid.Empty ||
            entity.Comp.BodyId == Guid.Empty ||
            _mobState.IsDead(entity.Owner))
        {
            return false;
        }

        var connection = _adapter.Connection;
        var boundWorld = _authority.BoundWorld;
        if (connection is not { IsConnected: true } ||
            connection.ConnectionId == Guid.Empty ||
            !boundWorld.HasValue)
        {
            return false;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        var agentId = AgentId.FromGuid(entity.Comp.AgentId);
        var bodyId = BodyId.FromGuid(entity.Comp.BodyId);
        var lease = _authority.ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue ||
            lease.Value.BodyId != bodyId ||
            lease.Value.Generation == 0)
        {
            return false;
        }

        context = new PassiveContext(
            connection,
            boundWorld.Value,
            connectionId,
            agentId,
            bodyId,
            lease.Value.Generation,
            new SimTick((ulong)_timing.CurTick.Value));
        return true;
    }

    private void Publish(
        PassiveContext context,
        PerceptionCategory category,
        PerceptualEvent perceptualEvent)
    {
        context.Connection.EnqueueEnvironmentMessage(new PerceptionMessage
        {
            WorldId = context.WorldId,
            ConnectionId = context.ConnectionId,
            Tick = context.Tick,
            SourceSequence = SourceSequence.Unassigned,
            LatestAck = default,
            AgentId = context.AgentId,
            PerceptId = PerceptId.NewId(),
            Category = category,
            Data = PerceptualEventWireCodec.Encode(perceptualEvent),
            Format = PerceptualEventWireCodec.Format,
        });

        var transmission = perceptualEvent.Transmission;
        var mutationKinds = transmission.MutationKinds.Count == 0
            ? "none"
            : string.Join(",", transmission.MutationKinds);
        var sourceDescription = perceptualEvent.SourceRef?.ToString()
            ?? (transmission.SourceResolution == CueSourceResolution.Self ? "self" : "anonymous");

        _sawmill.Debug(
            "Published passive perception: agent={0}, type={1}, category={2}, event={3}, source_ref={4}, profile={5}, channel={6}, range_fraction={7}, los={8}, occlusion={9}, payload_integrity={10}, payload_mutated={11}, mutations={12}, source_resolution={13}, direction_resolution={14}",
            context.AgentId,
            perceptualEvent.ObservationType,
            category,
            perceptualEvent.EventClass,
            sourceDescription,
            transmission.EmbodimentProfile,
            transmission.Channel,
            transmission.RangeFraction?.ToString("F3") ?? "n/a",
            transmission.LineOfSight?.ToString() ?? "n/a",
            transmission.Occlusion?.ToString("F3") ?? "n/a",
            transmission.PayloadIntegrity.ToString("F3"),
            transmission.PayloadMutated,
            mutationKinds,
            transmission.SourceResolution,
            transmission.DirectionResolution);
    }

    private enum BodyCueKind
    {
        Disturbance,
        Recovery,
    }

    private readonly record struct BodyCueKey(
        EntityUid Entity,
        BodyCueKind Kind);

    private sealed class PendingBodyCue
    {
        public PendingBodyCue(TimeSpan observedAt)
        {
            FirstObservedAt = observedAt;
            LastObservedAt = observedAt;
        }

        public TimeSpan FirstObservedAt { get; }
        public TimeSpan LastObservedAt { get; set; }
    }

    private readonly record struct PassiveContext(
        COGRConnectionManager Connection,
        WorldId WorldId,
        ConnectionId ConnectionId,
        AgentId AgentId,
        BodyId BodyId,
        uint BodyGeneration,
        SimTick Tick);
}
