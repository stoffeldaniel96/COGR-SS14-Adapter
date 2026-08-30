using System.Linq;
using COGR.Core.Actions;
using COGR.Core.Identifiers;
using COGR.Core.Time;
using Content.Shared.COGR.Components;
using Content.Shared.Interaction;

namespace Content.Server.COGR.Actions;

/// <summary>
/// Maintains physical body orientation toward one currently perceived opaque environment reference.
/// The handler never steers, paths, follows, or retains a host target identity across ticks.
/// </summary>
public sealed class COGRSustainedOrientationHandler
{
    private const double AlignmentToleranceRadians = 0.01;
    private readonly Dictionary<ActionProposalId, ActiveOrientation> _active = new();

    /// <summary>Starts one sustained orientation action after current perception and reference authority are verified.</summary>
    public ActionExecutionResult Start(
        ActionAttempt attempt,
        IEntityManager entityManager,
        EnvironmentRef targetRef,
        Func<ActionAttempt, EnvironmentRef, EntityUid?> resolveReference,
        Func<ActionAttempt, EnvironmentRef, bool> isCurrentlyObserved)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(entityManager);
        ArgumentNullException.ThrowIfNull(resolveReference);
        ArgumentNullException.ThrowIfNull(isCurrentlyObserved);

        var actor = GetEntityForBody(attempt.BodyId, entityManager);
        if (actor is null)
            return ActionExecutionResult.Failed(ActionFailureReason.BodyDied, "Body entity not found");

        if (!isCurrentlyObserved(attempt, targetRef))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetMovedOutOfRange,
                "Orientation target is not present in the current actor-relative semantic replica");
        }

        var target = resolveReference(attempt, targetRef);
        if (target is null || !entityManager.EntityExists(target.Value))
            return ActionExecutionResult.Failed(ActionFailureReason.TargetRemoved, "Orientation target reference could not be resolved");

        var faceResult = TryFaceCurrentTarget(actor.Value, target.Value, entityManager);
        if (faceResult is { } faceFailure)
            return faceFailure;

        _active[attempt.ProposalId] = new ActiveOrientation
        {
            ProposalId = attempt.ProposalId,
            BodyId = attempt.BodyId,
            Actor = actor.Value,
            TargetRef = targetRef,
        };

        return ActionExecutionResult.Started();
    }

    /// <summary>Ticks active orientations and returns only terminal lifecycle results.</summary>
    public IReadOnlyList<ActionResult> Tick(
        ulong currentTick,
        IEntityManager entityManager,
        IActiveActionRegistry registry,
        Func<ActionAttempt, EnvironmentRef, EntityUid?> resolveReference,
        Func<ActionAttempt, EnvironmentRef, bool> isCurrentlyObserved,
        Func<ActionAttempt, bool> hasCurrentAuthority)
    {
        ArgumentNullException.ThrowIfNull(entityManager);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolveReference);
        ArgumentNullException.ThrowIfNull(isCurrentlyObserved);
        ArgumentNullException.ThrowIfNull(hasCurrentAuthority);

        var results = new List<ActionResult>();
        var completed = new List<ActionProposalId>();
        foreach (var (proposalId, orientation) in _active)
        {
            var result = Progress(
                proposalId,
                orientation,
                currentTick,
                entityManager,
                registry,
                resolveReference,
                isCurrentlyObserved,
                hasCurrentAuthority);
            if (result is null)
                continue;

            completed.Add(proposalId);
            results.Add(result);
        }

        foreach (var proposalId in completed)
            _active.Remove(proposalId);

        return results;
    }

    /// <summary>Forgets one tracked orientation without changing locomotion or body state.</summary>
    public void Cleanup(ActionProposalId proposalId) => _active.Remove(proposalId);

    /// <summary>Forgets every tracked orientation owned by one body.</summary>
    public void CleanupAllForBody(BodyId bodyId)
    {
        var proposalIds = _active
            .Where(pair => pair.Value.BodyId == bodyId)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (var proposalId in proposalIds)
            _active.Remove(proposalId);
    }

    private static ActionResult? Progress(
        ActionProposalId proposalId,
        ActiveOrientation orientation,
        ulong currentTick,
        IEntityManager entityManager,
        IActiveActionRegistry registry,
        Func<ActionAttempt, EnvironmentRef, EntityUid?> resolveReference,
        Func<ActionAttempt, EnvironmentRef, bool> isCurrentlyObserved,
        Func<ActionAttempt, bool> hasCurrentAuthority)
    {
        var tick = new SimTick(currentTick);
        var attempt = registry.GetAction(proposalId);
        if (attempt is null)
            return ActionResult.Cancelled(proposalId, tick, "Sustained orientation commitment ended");

        if (!hasCurrentAuthority(attempt))
        {
            registry.UpdateState(proposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                proposalId,
                tick,
                ActionFailureReason.BodyAuthorityRevoked,
                "Body authority changed while sustained orientation was active");
        }

        if (!entityManager.EntityExists(orientation.Actor))
        {
            registry.UpdateState(proposalId, ActionState.Failed, tick);
            return ActionResult.Failed(proposalId, tick, ActionFailureReason.BodyDied, "Body entity deleted");
        }

        if (GetEntityForBody(orientation.BodyId, entityManager) != orientation.Actor)
        {
            registry.UpdateState(proposalId, ActionState.Failed, tick);
            return ActionResult.Failed(proposalId, tick, ActionFailureReason.BodyReplaced, "Body replaced");
        }

        if (!isCurrentlyObserved(attempt, orientation.TargetRef))
        {
            registry.UpdateState(proposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                proposalId,
                tick,
                ActionFailureReason.TargetMovedOutOfRange,
                "Orientation target left the current actor-relative semantic replica");
        }

        var target = resolveReference(attempt, orientation.TargetRef);
        if (target is null || !entityManager.EntityExists(target.Value))
        {
            registry.UpdateState(proposalId, ActionState.Failed, tick);
            return ActionResult.Failed(proposalId, tick, ActionFailureReason.TargetRemoved, "Orientation target was removed");
        }

        var faceResult = TryFaceCurrentTarget(orientation.Actor, target.Value, entityManager);
        if (faceResult is { } faceFailure)
        {
            registry.UpdateState(proposalId, ActionState.Failed, tick);
            return ActionResult.Failed(
                proposalId,
                tick,
                faceFailure.FailureReason ?? ActionFailureReason.InteractionBlocked,
                faceFailure.Detail);
        }

        if (attempt.State == ActionState.Started)
            registry.UpdateState(proposalId, ActionState.Progressing, tick);

        return null;
    }

    private static ActionExecutionResult? TryFaceCurrentTarget(
        EntityUid actor,
        EntityUid target,
        IEntityManager entityManager)
    {
        if (!entityManager.TrySystem<SharedTransformSystem>(out var transformSystem))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "TransformSystem not available");
        if (!entityManager.TrySystem<RotateToFaceSystem>(out var rotateSystem))
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "RotateToFaceSystem not available");
        if (!entityManager.TryGetComponent<TransformComponent>(actor, out var actorXform)
            || !entityManager.TryGetComponent<TransformComponent>(target, out var targetXform))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetLocationInvalidated,
                "Actor or orientation target has no transform");
        }

        var actorCoordinates = transformSystem.GetMapCoordinates(actor, xform: actorXform);
        var targetCoordinates = transformSystem.GetMapCoordinates(target, xform: targetXform);
        if (actorCoordinates.MapId != targetCoordinates.MapId)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.TargetMovedOutOfRange,
                "Actor and orientation target no longer share a map");
        }

        var offset = targetCoordinates.Position - actorCoordinates.Position;
        if (offset.LengthSquared() <= 0.01f)
            return null;

        var desiredAngle = Angle.FromWorldVec(offset);
        var currentAngle = transformSystem.GetWorldRotation(actorXform);
        if (Math.Abs(Angle.ShortestDistance(currentAngle, desiredAngle).Theta) <= AlignmentToleranceRadians)
            return null;

        return rotateSystem.TryFaceCoordinates(actor, targetCoordinates.Position, actorXform)
            ? null
            : ActionExecutionResult.Failed(ActionFailureReason.InteractionBlocked, "Body cannot change facing direction");
    }

    private static EntityUid? GetEntityForBody(BodyId bodyId, IEntityManager entityManager)
    {
        var query = entityManager.AllEntityQueryEnumerator<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (component.BodyId == bodyId.ToGuid())
                return uid;
        }

        return null;
    }

    private sealed class ActiveOrientation
    {
        public required ActionProposalId ProposalId { get; init; }
        public required BodyId BodyId { get; init; }
        public required EntityUid Actor { get; init; }
        public required EnvironmentRef TargetRef { get; init; }
    }
}
