using System.Linq;
using System.Numerics;
using System.Text.Json;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Time;
using Content.Server.COGR.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Events;
using Content.Server.NPC.Systems;
using Content.Shared.COGR.Components;
using Content.Shared.Movement.Components;
using Content.Shared.NPC;

namespace Content.Server.COGR.Actions;

public sealed partial class COGRActionExecutor
{
    private const ulong DirectionalSteeringProgressCheckTicks = 30;
    private const float DirectionalSteeringMinimumForwardProgress = 0.05f;
    private const int DirectionalSteeringMaximumStallChecks = 6;

    [Dependency] private NPCSteeringSystem _npcSteering = default!;
    [Dependency] private COGRSemanticReplicaSystem _semanticReplica = default!;

    private readonly Dictionary<ActionProposalId, ActiveDirectionalSteering> _directionalSteering = new();
    private readonly Dictionary<EntityUid, ActionProposalId> _directionalSteeringByEntity = new();

    private void InitializeDirectionalSteering()
    {
        SubscribeLocalEvent<COGRControlledComponent, NPCSteeringEvent>(OnCogRDirectionalSteering);
    }

    private ActionExecutionResult StartDirectionalSteering(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<SteerRelativeActionParams>(attempt.Parameters);
        if (parameters is null
            || !Enum.IsDefined(parameters.Bearing)
            || parameters.Bearing == BodyRelativeBearing.Unknown)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Invalid relative steering parameters");
        }

        var entity = ResolveDirectionalSteeringBody(attempt.BodyId);
        if (!entity.HasValue)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.BodyDied,
                "Body entity not found");
        }

        if (!TryComp(entity.Value, out TransformComponent? xform))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.BodyDied,
                "Body has no transform");
        }

        var parentDirection = BearingToParentSteeringDirection(parameters.Bearing, xform.LocalRotation);
        if (parentDirection == Vector2.Zero)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Relative steering bearing has no planar direction");
        }

        var requestedProgress = parameters.MaximumDistance ?? COGRSpatialPolicy.MaximumDirectionalSteeringProgress;
        if (!double.IsFinite(requestedProgress) || requestedProgress <= 0)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Relative steering progress horizon must be finite and positive");
        }

        var progressHorizon = (float)Math.Min(
            requestedProgress,
            COGRSpatialPolicy.MaximumDirectionalSteeringProgress);

        EnsureComp<InputMoverComponent>(entity.Value);
        EnsureComp<MovementSpeedModifierComponent>(entity.Value);
        EnsureComp<MobMoverComponent>(entity.Value);
        EnsureComp<ActiveNPCComponent>(entity.Value);

        // A direction-only objective deliberately has no destination coordinate. Register the current coordinates only as
        // the NPC steering system's required live-body anchor; the event override below disables coordinate seeking and
        // contributes the cognitive bearing directly to native context steering alongside collision/separation danger.
        _npcSteering.Unregister(entity.Value);
        EnsureComp<ActiveNPCComponent>(entity.Value);
        var steering = _npcSteering.Register(entity.Value, xform.Coordinates);
        steering.Status = SteeringStatus.Moving;

        var startTick = (ulong)_timing.CurTick.Value;
        var active = new ActiveDirectionalSteering
        {
            ProposalId = attempt.ProposalId,
            BodyId = attempt.BodyId,
            Entity = entity.Value,
            ParentUid = xform.ParentUid,
            StartPosition = xform.LocalPosition,
            ParentDirection = Vector2.Normalize(parentDirection),
            ProgressHorizon = progressHorizon,
            LastProgressCheckTick = startTick,
        };

        // Temporary live-acceptance diagnostic. Directional steering intentionally has no native seek target, so expose
        // the exact Station realization that does exist: current SS14 coordinates, body rotation, the parent-frame direction
        // injected into NPC steering, and the bounded progress horizon. The horizon point is diagnostic-only and is never
        // supplied to pathfinding/steering as a destination.
        var diagnosticHorizonPoint = active.StartPosition + active.ParentDirection * active.ProgressHorizon;
        _sawmill.Info(
            "COGR relative steering realization: proposal={0} agent={1} body={2} causalTrace={3} startCoordinates={4} parent={5} localRotation={6} bearing={7} parentDirection=({8:F4},{9:F4}) progressHorizon={10:F3} diagnosticHorizonPoint=({11:F3},{12:F3}) seekTarget=false",
            attempt.ProposalId,
            attempt.AgentId,
            attempt.BodyId,
            attempt.CausalTraceId,
            xform.Coordinates,
            xform.ParentUid,
            xform.LocalRotation,
            parameters.Bearing,
            active.ParentDirection.X,
            active.ParentDirection.Y,
            active.ProgressHorizon,
            diagnosticHorizonPoint.X,
            diagnosticHorizonPoint.Y);

        LogAdapterLandmarkRealization(attempt, xform);

        _directionalSteering[attempt.ProposalId] = active;
        _directionalSteeringByEntity[entity.Value] = attempt.ProposalId;
        return ActionExecutionResult.Started();
    }

    /// <summary>
    /// Logs the adapter-authoritative realization of the exact current bounded replica at the moment a COGR relative
    /// steering action starts. This Station-only diagnostic resolves opaque references under the accepted action's exact
    /// authority and never transmits host coordinates or entity identity back into cognition.
    /// </summary>
    private void LogAdapterLandmarkRealization(ActionAttempt attempt, TransformComponent actorTransform)
    {
        var observations = _semanticReplica.GetCurrentObservationsForDiagnostic(
            attempt.AuthorityLease.ConnectionId,
            attempt.AgentId,
            attempt.BodyId,
            attempt.AuthorityLease.Generation);

        var landmarks = observations.Select(observation =>
        {
            var target = _referenceResolver?.Invoke(attempt, observation.EnvironmentRef);
            TransformComponent? targetTransform = null;
            if (target.HasValue)
                _ = TryComp(target.Value, out targetTransform);

            return new
            {
                environmentReference = observation.EnvironmentRef.ToString(),
                category = observation.Category ?? "none",
                projectedLocation = observation.Location?.ToString(),
                features = observation.Features.Select(static feature => new
                {
                    category = feature.Category,
                    type = feature.FeatureType,
                    value = feature.Value?.ToString(),
                    confidence = feature.Confidence,
                }).ToArray(),
                resolved = targetTransform is not null,
                coordinates = targetTransform?.Coordinates.ToString(),
                parent = targetTransform?.ParentUid.ToString(),
                localX = targetTransform?.LocalPosition.X,
                localY = targetTransform?.LocalPosition.Y,
            };
        }).ToArray();

        var json = JsonSerializer.Serialize(new
        {
            kind = "remembered_navigation_adapter_landmarks",
            proposalId = attempt.ProposalId.ToString(),
            agentId = attempt.AgentId.ToString(),
            actor = new
            {
                coordinates = actorTransform.Coordinates.ToString(),
                parent = actorTransform.ParentUid.ToString(),
                localX = actorTransform.LocalPosition.X,
                localY = actorTransform.LocalPosition.Y,
                localRotation = actorTransform.LocalRotation.ToString(),
            },
            landmarks,
        });

        _sawmill.Info("COGR route adapter landmarks: {0}", json);
    }

    private void OnCogRDirectionalSteering(
        EntityUid uid,
        COGRControlledComponent component,
        ref NPCSteeringEvent args)
    {
        if (!_directionalSteeringByEntity.TryGetValue(uid, out var proposalId)
            || !_directionalSteering.TryGetValue(proposalId, out var active))
        {
            return;
        }

        // ParentDirection is already in the same grid/input frame used by NPCSteeringSystem.Directions. Applying
        // args.OffsetRotation again would rotate the cognitive bearing twice on rotated grids.
        var desired = active.ParentDirection;
        for (var index = 0; index < SharedNPCSteeringSystem.InterestDirections; index++)
        {
            // Mirror NPCSteeringSystem.ApplySeek: cognitive bearing supplies interest while native collision avoidance and
            // separation remain authoritative contributors. This is steering evidence, not a hidden target coordinate.
            var dot = Vector2.Dot(desired, NPCSteeringSystem.Directions[index]);
            var interest = Math.Clamp((dot + 1f) * 0.5f, 0f, 1f);
            args.Steering.Interest[index] = MathF.Max(args.Steering.Interest[index], interest);
        }

        args.Steering.CanSeek = false;
    }

    private IReadOnlyList<ActionResult> TickDirectionalSteering(ulong currentTick)
    {
        if (_directionalSteering.Count == 0)
            return Array.Empty<ActionResult>();

        var results = new List<ActionResult>();
        foreach (var active in _directionalSteering.Values.ToArray())
        {
            var result = TickDirectionalSteering(active, currentTick);
            if (result is not null)
                results.Add(result);
        }

        return results;
    }

    private ActionResult? TickDirectionalSteering(
        ActiveDirectionalSteering active,
        ulong currentTick)
    {
        var attempt = _actionRegistry.GetAction(active.ProposalId);
        if (attempt is null)
        {
            CleanupDirectionalSteering(active.ProposalId);
            return null;
        }

        var tick = new SimTick(currentTick);
        if (!Exists(active.Entity))
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.BodyDied,
                "Body entity was deleted during directional steering");
        }

        var currentBody = ResolveDirectionalSteeringBody(active.BodyId);
        if (currentBody != active.Entity)
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.BodyReplaced,
                "Body authority now resolves to a different entity");
        }

        if (!TryComp(active.Entity, out TransformComponent? xform)
            || xform.ParentUid != active.ParentUid)
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.NoPathFound,
                "Directional steering spatial frame changed before reevaluation");
        }

        if (!TryComp<NPCSteeringComponent>(active.Entity, out var steering)
            || steering.Status == SteeringStatus.NoPath)
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                active.ProposalId,
                tick,
                ActionFailureReason.NoPathFound,
                "Native directional steering cannot make progress");
        }

        var forwardProgress = Vector2.Dot(
            xform.LocalPosition - active.StartPosition,
            active.ParentDirection);
        if (forwardProgress > active.BestForwardProgress)
            active.BestForwardProgress = forwardProgress;

        if (active.BestForwardProgress >= active.ProgressHorizon)
        {
            CleanupDirectionalSteering(active.ProposalId);
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Completed, tick);
            return ActionResult.Completed(
                active.ProposalId,
                tick,
                detail: "Bounded qualitative steering progress reached; cognition should reassess current scene evidence");
        }

        if (currentTick - active.LastProgressCheckTick >= DirectionalSteeringProgressCheckTicks)
        {
            var gained = active.BestForwardProgress - active.LastObservedBestForwardProgress;
            if (gained < DirectionalSteeringMinimumForwardProgress)
            {
                active.ConsecutiveStallChecks++;
                if (active.ConsecutiveStallChecks >= DirectionalSteeringMaximumStallChecks)
                {
                    CleanupDirectionalSteering(active.ProposalId);
                    _actionRegistry.UpdateState(active.ProposalId, ActionState.Failed, tick);
                    return ActionResult.Failed(
                        active.ProposalId,
                        tick,
                        ActionFailureReason.NoPathFound,
                        "Directional steering made no net progress along the requested bearing");
                }
            }
            else
            {
                active.ConsecutiveStallChecks = 0;
            }

            active.LastObservedBestForwardProgress = active.BestForwardProgress;
            active.LastProgressCheckTick = currentTick;
        }

        if (attempt.State == ActionState.Started)
            _actionRegistry.UpdateState(active.ProposalId, ActionState.Progressing, tick);

        return null;
    }

    private void CleanupDirectionalSteering(ActionProposalId proposalId)
    {
        if (!_directionalSteering.Remove(proposalId, out var active))
            return;

        _directionalSteeringByEntity.Remove(active.Entity);
        if (Exists(active.Entity))
        {
            _npcSteering.Unregister(active.Entity);
            RemComp<ActiveNPCComponent>(active.Entity);
            if (TryComp<InputMoverComponent>(active.Entity, out var mover))
            {
                mover.CurTickSprintMovement = Vector2.Zero;
                mover.CurTickWalkMovement = Vector2.Zero;
                Dirty(active.Entity, mover);
            }
        }
    }

    private EntityUid? ResolveDirectionalSteeringBody(BodyId bodyId)
    {
        var query = AllEntityQuery<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var controlled))
        {
            if (controlled.BodyId == bodyId.ToGuid() && controlled.IsActive)
                return uid;
        }

        return null;
    }

    private static Vector2 BearingToParentSteeringDirection(
        BodyRelativeBearing bearing,
        Angle localRotation)
    {
        const float diagonal = 0.70710677f;
        var ownerRelative = bearing switch
        {
            BodyRelativeBearing.Forward => new Vector2(1, 0),
            BodyRelativeBearing.ForwardLeft => new Vector2(diagonal, diagonal),
            BodyRelativeBearing.Left => new Vector2(0, 1),
            BodyRelativeBearing.BackLeft => new Vector2(-diagonal, diagonal),
            BodyRelativeBearing.Back => new Vector2(-1, 0),
            BodyRelativeBearing.BackRight => new Vector2(-diagonal, -diagonal),
            BodyRelativeBearing.Right => new Vector2(0, -1),
            BodyRelativeBearing.ForwardRight => new Vector2(diagonal, -diagonal),
            _ => Vector2.Zero,
        };
        if (ownerRelative == Vector2.Zero)
            return Vector2.Zero;

        var cos = (float)Math.Cos(localRotation.Theta);
        var sin = (float)Math.Sin(localRotation.Theta);
        return new Vector2(
            ownerRelative.X * cos - ownerRelative.Y * sin,
            ownerRelative.X * sin + ownerRelative.Y * cos);
    }

    private sealed class ActiveDirectionalSteering
    {
        internal required ActionProposalId ProposalId { get; init; }
        internal required BodyId BodyId { get; init; }
        internal required EntityUid Entity { get; init; }
        internal required EntityUid ParentUid { get; init; }
        internal required Vector2 StartPosition { get; init; }
        internal required Vector2 ParentDirection { get; init; }
        internal required float ProgressHorizon { get; init; }
        internal float BestForwardProgress { get; set; }
        internal float LastObservedBestForwardProgress { get; set; }
        internal ulong LastProgressCheckTick { get; set; }
        internal int ConsecutiveStallChecks { get; set; }
    }
}
