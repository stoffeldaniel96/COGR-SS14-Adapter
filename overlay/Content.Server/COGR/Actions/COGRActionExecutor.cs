using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using EnvironmentReferenceId = COGR.Core.Identifiers.EnvironmentRef;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using Content.Shared.COGR.Components;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Actions;

/// <summary>
/// F02 implementation of COGR action executor for SS14.
/// Handles the complete 10-state action lifecycle with authority validation,
/// conflict detection, and timeout enforcement.
/// </summary>
public sealed partial class COGRActionExecutor : EntitySystem
{
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    private ISawmill _sawmill = default!;
    private readonly COGRActionRegistry _actionRegistry;
    private readonly COGRMovementHandler _movementHandler;
    private readonly COGRInteractionHandler _interactionHandler;
    private readonly Dictionary<BodyId, BodyAuthorityData> _bodyAuthority = new();
    
    // Reference resolver delegate - receives the exact accepted action so the adapter can
    // enforce connection, body, agent, and authority-generation scope.
    private Func<ActionAttempt, EnvironmentReferenceId, EntityUid?>? _referenceResolver;

    // F02 configuration
    private const uint DefaultTimeoutMs = 30000; // 30 seconds
    private const double MsPerTick = 16.67; // ~60 ticks/second

    public COGRActionExecutor()
    {
        _actionRegistry = new COGRActionRegistry();
        _movementHandler = new COGRMovementHandler();
        _interactionHandler = new COGRInteractionHandler();
    }
    
    /// <summary>
    /// Sets the reference resolver function for resolving environment references to entities
    /// under the exact accepted action context.
    /// </summary>
    public void SetReferenceResolver(
        Func<ActionAttempt, EnvironmentReferenceId, EntityUid?> resolver)
    {
        _referenceResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public override void Initialize()
    {
        base.Initialize();
        InitializeDirectionalSteering();
        _sawmill = _logManager.GetSawmill("cogr.actions.f02");
        _sawmill.Info("F02 COGR Action Executor initialized");
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // F02 Action Lifecycle API
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Proposes an action for execution. Returns accept/reject disposition.
    /// </summary>
    public ActionProposalResult ProposeAction(ActionAttempt attempt)
    {
        // 1. Validate authority
        var authValidation = ValidateAuthority(attempt);
        if (!authValidation.IsValid)
        {
            _sawmill.Debug("Action {0} rejected: {1}", attempt.ProposalId, authValidation.Detail);
            return ActionProposalResult.Rejected(
                authValidation.RejectionReason!.Value,
                authValidation.Detail);
        }

        // 2. Check for conflicting actions
        if (attempt.Capability != ActionCapability.MovementStop &&
            attempt.Capability != ActionCapability.ActionCancel)
        {
            var conflict = _actionRegistry.GetConflictingAction(attempt.BodyId, attempt.Capability);
            if (conflict != null)
            {
                _sawmill.Debug("Action {0} conflicts with {1}", attempt.ProposalId, conflict.ProposalId);
                return ActionProposalResult.Rejected(
                    ActionRejectionReason.ConflictingActionInProgress,
                    $"Conflicting action {conflict.ProposalId} is active");
            }
        }

        // 3. Validate capability-specific parameters
        var capabilityValidation = ValidateCapability(attempt);
        if (!capabilityValidation.IsValid)
        {
            _sawmill.Debug("Action {0} invalid params: {1}", attempt.ProposalId, capabilityValidation.Detail);
            return ActionProposalResult.Rejected(
                capabilityValidation.Reason,
                capabilityValidation.Detail);
        }

        // 4. Accept the action
        _actionRegistry.Register(attempt);
        var currentTick = (ulong)_timing.CurTick.Value;
        _actionRegistry.UpdateState(attempt.ProposalId, ActionState.Accepted, new SimTick(currentTick));

        _sawmill.Info("Action {0} accepted: {1}", attempt.ProposalId, attempt.Capability);
        return ActionProposalResult.Accepted();
    }

    /// <summary>
    /// Starts execution of an accepted action.
    /// </summary>
    public ActionExecutionResult StartAction(ActionProposalId proposalId)
    {
        var attempt = _actionRegistry.GetAction(proposalId);
        if (attempt == null)
        {
            return ActionExecutionResult.NotFound();
        }

        if (attempt.State != ActionState.Accepted)
        {
            return ActionExecutionResult.InvalidState(attempt.State);
        }

        var currentTick = (ulong)_timing.CurTick.Value;

        // Authority can rotate after proposal acceptance. Revalidate immediately before any
        // native SS14 action begins so an accepted stale lease cannot execute.
        var authValidation = ValidateAuthority(attempt);
        if (!authValidation.IsValid)
        {
            _actionRegistry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            _actionRegistry.Remove(proposalId);

            var failureReason = authValidation.RejectionReason == ActionRejectionReason.ConnectionNotAuthorized
                ? ActionFailureReason.ConnectionLost
                : ActionFailureReason.BodyAuthorityRevoked;

            _sawmill.Info(
                "Action {0} failed before execution because authority changed: {1}",
                proposalId,
                authValidation.Detail);
            return ActionExecutionResult.Failed(failureReason, authValidation.Detail);
        }

        _actionRegistry.UpdateState(proposalId, ActionState.Started, new SimTick(currentTick));

        // Route to capability handler
        var result = attempt.Capability switch
        {
            // Movement actions
            ActionCapability.MovementTurn => _movementHandler.ExecuteTurn(attempt, EntityManager),
            ActionCapability.MovementStep => _movementHandler.ExecuteStep(attempt, EntityManager),
            ActionCapability.MovementSteerRelative => StartDirectionalSteering(attempt),
            ActionCapability.MovementSteerToBodyRelativePoint => StartProjectedObjectiveSteering(attempt),
            ActionCapability.MovementStop => ExecuteMovementStop(attempt),
            ActionCapability.MovementMoveToLocation => _movementHandler.StartMoveToLocation(attempt, EntityManager),
            ActionCapability.MovementEstablishSpatialRelation => ExecuteEstablishSpatialRelation(attempt),
            ActionCapability.MovementMaintainOrientationToReference => ExecuteMaintainOrientation(attempt),
            
            // Control actions
            ActionCapability.ActionCancel => ExecuteCancel(attempt),

            // Communication actions
            ActionCapability.CommunicationSpeakLocal => ExecuteSpeakLocal(attempt),
            
            // Interaction actions (F3/F5 scope)
            ActionCapability.InteractionOpen => ExecuteOpenInteraction(attempt),
            ActionCapability.InteractionClose => ExecuteCloseInteraction(attempt),
            ActionCapability.InteractionIngest => ExecuteIngestInteraction(attempt),
            
            // Manipulation actions (F3/F5 scope)
            ActionCapability.ManipulationPickUp => ExecuteAcquire(attempt),
            ActionCapability.ManipulationDrop => ExecuteDrop(attempt),
            ActionCapability.ManipulationPlaceNear => ExecutePlaceNear(attempt),
            
            _ => ActionExecutionResult.UnsupportedCapability(attempt.Capability)
        };

        // If action completed immediately (turn, step), update state and remove from registry
        if (result.IsSuccess && !result.IsStarted)
        {
            _actionRegistry.UpdateState(proposalId, ActionState.Completed, new SimTick(currentTick));
            _actionRegistry.Remove(proposalId);
            _sawmill.Info("Action {0} completed immediately", proposalId);
        }
        // If action failed immediately, remove from registry
        else if (!result.IsSuccess && !result.IsStarted)
        {
            _actionRegistry.UpdateState(proposalId, ActionState.Failed, new SimTick(currentTick));
            _actionRegistry.Remove(proposalId);
            _sawmill.Info("Action {0} failed: {1}", proposalId, result.Detail);
        }
        else if (result.IsStarted)
        {
            _sawmill.Info("Action {0} started (async)", proposalId);
        }

        return result;
    }

    /// <summary>
    /// Ticks active actions and checks for timeouts.
    /// </summary>
    public IReadOnlyList<ActionResult> TickActions(ulong currentTick)
    {
        var results = new List<ActionResult>();

        // Check for timeouts
        var timedOut = _actionRegistry.GetTimedOut(new SimTick(currentTick), DefaultTimeoutMs, MsPerTick);
        foreach (var attempt in timedOut)
        {
            _actionRegistry.UpdateState(attempt.ProposalId, ActionState.TimedOut, new SimTick(currentTick));
            
            // Cleanup capability-specific native tracking before forgetting the action.
            _movementHandler.CleanupMovement(attempt.ProposalId, attempt.BodyId, EntityManager);
            _relativeSpatialMovementHandler.CleanupMovement(attempt.ProposalId, EntityManager);
            CleanupDirectionalSteering(attempt.ProposalId);
            CleanupProjectedObjectiveSteering(attempt.ProposalId);
            _sustainedOrientationHandler.Cleanup(attempt.ProposalId);
            CleanupAcquisition(attempt.ProposalId);
            
            _actionRegistry.Remove(attempt.ProposalId);
            results.Add(ActionResult.TimedOut(attempt.ProposalId, new SimTick(currentTick)));
            _sawmill.Warning("Action {0} timed out", attempt.ProposalId);
        }

        // Tick handlers for active asynchronous realizations.
        var movementResults = _movementHandler.TickMovements(currentTick, EntityManager, _actionRegistry);
        results.AddRange(movementResults);
        var relativeSpatialResults = _relativeSpatialMovementHandler.Tick(currentTick, EntityManager, _actionRegistry);
        results.AddRange(relativeSpatialResults);
        var directionalSteeringResults = TickDirectionalSteering(currentTick);
        results.AddRange(directionalSteeringResults);
        var projectedObjectiveSteeringResults = TickProjectedObjectiveSteering(currentTick);
        results.AddRange(projectedObjectiveSteeringResults);
        var orientationResults = TickSustainedOrientations(currentTick);
        results.AddRange(orientationResults);
        var acquisitionResults = TickAcquisitions(currentTick);
        results.AddRange(acquisitionResults);

        return results;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Body Authority Management
    // ═══════════════════════════════════════════════════════════════════════════

    public void RegisterAgentBody(AgentId agentId, BodyId bodyId, ConnectionId connectionId)
    {
        if (!_bodyAuthority.TryGetValue(bodyId, out var data))
        {
            data = new BodyAuthorityData
            {
                BodyId = bodyId,
                AgentId = agentId,
                ConnectionId = connectionId,
                Generation = 1,
                GrantedAtTick = (ulong)_timing.CurTick.Value
            };
            _bodyAuthority[bodyId] = data;
            _sawmill.Info("Registered body authority: {0} -> {1} (gen 1)", bodyId, agentId);
        }
        else
        {
            // Update existing authority (generation increments)
            data.AgentId = agentId;
            data.ConnectionId = connectionId;
            data.Generation++;
            data.GrantedAtTick = (ulong)_timing.CurTick.Value;
            _sawmill.Info("Updated body authority: {0} -> {1} (gen {2})", bodyId, agentId, data.Generation);
        }
    }

    public void RevokeBodyAuthority(BodyId bodyId)
    {
        if (_bodyAuthority.TryGetValue(bodyId, out var data))
        {
            data.Generation++;
            _sawmill.Info("Revoked body authority: {0} (gen now {1})", bodyId, data.Generation);
        }
    }

    public BodyAuthorityLease? GetBodyAuthority(BodyId bodyId)
    {
        if (!_bodyAuthority.TryGetValue(bodyId, out var data))
            return null;

        var lease = BodyAuthorityLease.Create(
            data.BodyId,
            data.AgentId,
            data.ConnectionId);
        
        // Adjust generation if needed
        if (data.Generation > 1)
        {
            for (uint i = 1; i < data.Generation; i++)
            {
                lease = lease.WithNextGeneration();
            }
        }
        
        return lease;
    }

    public IActiveActionRegistry ActionRegistry => _actionRegistry;

    // ═══════════════════════════════════════════════════════════════════════════
    // Private Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private AuthorityValidationResult ValidateAuthority(ActionAttempt attempt)
    {
        // Local diagnostic movement commands intentionally use an unassigned connection.
        // Exact connection equality is still enforced below, while target-bearing actions
        // additionally require the live adapter connection in ResolveActionTarget.
        if (attempt.AuthorityLease.BodyId != attempt.BodyId)
        {
            return AuthorityValidationResult.Invalid(
                ActionRejectionReason.NoBodyAuthority,
                "Authority lease does not belong to the requested body");
        }

        if (attempt.AuthorityLease.AgentId != attempt.AgentId)
        {
            return AuthorityValidationResult.Invalid(
                ActionRejectionReason.NoBodyAuthority,
                "Authority lease does not belong to the requesting agent");
        }

        if (!_bodyAuthority.TryGetValue(attempt.BodyId, out var currentAuth))
        {
            return AuthorityValidationResult.Invalid(
                ActionRejectionReason.NoBodyAuthority,
                "Body not registered");
        }

        if (currentAuth.AgentId != attempt.AgentId)
        {
            return AuthorityValidationResult.Invalid(
                ActionRejectionReason.NoBodyAuthority,
                "Agent does not control this body");
        }

        if (currentAuth.ConnectionId != attempt.AuthorityLease.ConnectionId)
        {
            return AuthorityValidationResult.Invalid(
                ActionRejectionReason.ConnectionNotAuthorized,
                "Authority lease belongs to a different connection");
        }

        if (currentAuth.Generation != attempt.AuthorityLease.Generation)
        {
            return AuthorityValidationResult.Invalid(
                ActionRejectionReason.BodyAuthorityGenerationMismatch,
                $"Stale authority generation (current: {currentAuth.Generation}, attempt: {attempt.AuthorityLease.Generation})");
        }

        return AuthorityValidationResult.Valid();
    }

    private CapabilityValidationResult ValidateCapability(ActionAttempt attempt)
    {
        return attempt.Capability switch
        {
            // Movement actions
            ActionCapability.MovementTurn => ValidateTurnParams(attempt.Parameters),
            ActionCapability.MovementStep => ValidateStepParams(attempt.Parameters),
            ActionCapability.MovementSteerRelative => ValidateSteerRelativeParams(attempt.Parameters),
            ActionCapability.MovementSteerToBodyRelativePoint => ValidateProjectedObjectiveSteeringParams(attempt.Parameters),
            ActionCapability.MovementStop => CapabilityValidationResult.Valid(),
            ActionCapability.MovementMoveToLocation => ValidateMoveToParams(attempt.Parameters),
            ActionCapability.MovementEstablishSpatialRelation => ValidateEstablishSpatialRelationParams(attempt.Parameters),
            ActionCapability.MovementMaintainOrientationToReference => ValidateMaintainOrientationParams(attempt.Parameters),
            
            // Control actions
            ActionCapability.ActionCancel => ValidateCancelParams(attempt.Parameters),

            // Communication actions
            ActionCapability.CommunicationSpeakLocal => ValidateSpeakLocalParams(attempt.Parameters),
            
            // Interaction actions
            ActionCapability.InteractionOpen => ValidateOpenParams(attempt.Parameters),
            ActionCapability.InteractionClose => ValidateCloseParams(attempt.Parameters),
            ActionCapability.InteractionIngest => ValidateIngestParams(attempt.Parameters),
            
            // Manipulation actions
            ActionCapability.ManipulationPickUp => ValidatePickUpParams(attempt.Parameters),
            ActionCapability.ManipulationDrop => CapabilityValidationResult.Valid(), // HandId is optional
            ActionCapability.ManipulationPlaceNear => ValidatePlaceNearParams(attempt.Parameters),
            
            _ => CapabilityValidationResult.Invalid(
                ActionRejectionReason.UnknownActionType,
                $"Unknown capability: {attempt.Capability}")
        };
    }

    private ActionExecutionResult ExecuteCancel(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<CancelActionParams>(attempt.Parameters);
        if (parameters == null)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Invalid cancel parameters");
        }

        var target = _actionRegistry.GetAction(parameters.TargetProposalId);
        if (target == null)
        {
            var currentTick = (ulong)_timing.CurTick.Value;
            _actionRegistry.UpdateState(attempt.ProposalId, ActionState.Failed, new SimTick(currentTick));
            _actionRegistry.Remove(attempt.ProposalId);
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Target action not found");
        }

        if (target.State.IsTerminal())
        {
            var currentTick = (ulong)_timing.CurTick.Value;
            _actionRegistry.UpdateState(attempt.ProposalId, ActionState.Failed, new SimTick(currentTick));
            _actionRegistry.Remove(attempt.ProposalId);
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Target action already terminal");
        }

        // Cancel native capability-specific tracking before removing the target action.
        CleanupActionTracking(target.ProposalId, target.BodyId);

        // Cancel the target
        var tick = (ulong)_timing.CurTick.Value;
        _actionRegistry.UpdateState(parameters.TargetProposalId, ActionState.Cancelled, new SimTick(tick));
        _actionRegistry.Remove(parameters.TargetProposalId);

        // Complete the cancel action
        _actionRegistry.UpdateState(attempt.ProposalId, ActionState.Completed, new SimTick(tick));
        _actionRegistry.Remove(attempt.ProposalId);

        return ActionExecutionResult.Completed(null);
    }

    private static CapabilityValidationResult ValidateTurnParams(ReadOnlyMemory<byte> parameters)
    {
        var p = ActionParameterSerializer.Deserialize<TurnActionParams>(parameters);
        return p != null
            ? CapabilityValidationResult.Valid()
            : CapabilityValidationResult.Invalid(ActionRejectionReason.InvalidParameters, "Invalid turn parameters");
    }

    private static CapabilityValidationResult ValidateStepParams(ReadOnlyMemory<byte> parameters)
    {
        var p = ActionParameterSerializer.Deserialize<StepActionParams>(parameters);
        return p != null
            ? CapabilityValidationResult.Valid()
            : CapabilityValidationResult.Invalid(ActionRejectionReason.InvalidParameters, "Invalid step parameters");
    }

    private static CapabilityValidationResult ValidateSteerRelativeParams(ReadOnlyMemory<byte> parameters)
    {
        var p = ActionParameterSerializer.Deserialize<SteerRelativeActionParams>(parameters);
        if (p is null
            || !Enum.IsDefined(p.Bearing)
            || p.Bearing == BodyRelativeBearing.Unknown
            || (p.MaximumDistance is { } maximumDistance
                && (!double.IsFinite(maximumDistance) || maximumDistance <= 0)))
        {
            return CapabilityValidationResult.Invalid(
                ActionRejectionReason.InvalidParameters,
                "Invalid relative steering parameters");
        }

        return CapabilityValidationResult.Valid();
    }

    private static CapabilityValidationResult ValidateMoveToParams(ReadOnlyMemory<byte> parameters)
    {
        var p = ActionParameterSerializer.Deserialize<MoveToLocationParams>(parameters);
        return p != null
            ? CapabilityValidationResult.Valid()
            : CapabilityValidationResult.Invalid(ActionRejectionReason.InvalidParameters, "Invalid move_to parameters");
    }

    private static CapabilityValidationResult ValidateCancelParams(ReadOnlyMemory<byte> parameters)
    {
        var p = ActionParameterSerializer.Deserialize<CancelActionParams>(parameters);
        return p != null
            ? CapabilityValidationResult.Valid()
            : CapabilityValidationResult.Invalid(ActionRejectionReason.InvalidParameters, "Invalid cancel parameters");
    }

    private static CapabilityValidationResult ValidateOpenParams(ReadOnlyMemory<byte> parameters)
    {
        var p = ActionParameterSerializer.Deserialize<OpenActionParams>(parameters);
        return p != null
            ? CapabilityValidationResult.Valid()
            : CapabilityValidationResult.Invalid(ActionRejectionReason.InvalidParameters, "Invalid open parameters");
    }

    private static CapabilityValidationResult ValidateCloseParams(ReadOnlyMemory<byte> parameters)
    {
        var p = ActionParameterSerializer.Deserialize<CloseActionParams>(parameters);
        return p != null
            ? CapabilityValidationResult.Valid()
            : CapabilityValidationResult.Invalid(ActionRejectionReason.InvalidParameters, "Invalid close parameters");
    }

    private static CapabilityValidationResult ValidatePickUpParams(ReadOnlyMemory<byte> parameters)
    {
        var p = ActionParameterSerializer.Deserialize<PickUpActionParams>(parameters);
        return p != null
            ? CapabilityValidationResult.Valid()
            : CapabilityValidationResult.Invalid(ActionRejectionReason.InvalidParameters, "Invalid acquisition parameters");
    }

    private static CapabilityValidationResult ValidatePlaceNearParams(ReadOnlyMemory<byte> parameters)
    {
        var p = ActionParameterSerializer.Deserialize<PlaceNearActionParams>(parameters);
        return p != null
            ? CapabilityValidationResult.Valid()
            : CapabilityValidationResult.Invalid(ActionRejectionReason.InvalidParameters, "Invalid place_near parameters");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Interaction Action Executors (Workstream H)
    // ═══════════════════════════════════════════════════════════════════════════

    private ActionExecutionResult ExecuteOpenInteraction(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<OpenActionParams>(attempt.Parameters);
        if (parameters == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid open parameters");
        }

        if (_referenceResolver == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Reference resolver not configured");
        }

        return _interactionHandler.ExecuteOpen(
            attempt,
            EntityManager,
            parameters.TargetRef,
            reference => _referenceResolver(attempt, reference));
    }

    private ActionExecutionResult ExecuteCloseInteraction(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<CloseActionParams>(attempt.Parameters);
        if (parameters == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid close parameters");
        }

        if (_referenceResolver == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Reference resolver not configured");
        }

        return _interactionHandler.ExecuteClose(
            attempt,
            EntityManager,
            parameters.TargetRef,
            reference => _referenceResolver(attempt, reference));
    }

    private ActionExecutionResult ExecutePickUp(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<PickUpActionParams>(attempt.Parameters);
        if (parameters == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid pick_up parameters");
        }

        if (_referenceResolver == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Reference resolver not configured");
        }

        return _interactionHandler.ExecutePickUp(
            attempt,
            EntityManager,
            parameters.TargetRef,
            reference => _referenceResolver(attempt, reference));
    }

    private ActionExecutionResult ExecuteDrop(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<DropActionParams>(attempt.Parameters);
        if (parameters == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid drop parameters");
        }

        return _interactionHandler.ExecuteDrop(attempt, EntityManager, parameters?.HandId);
    }

    private ActionExecutionResult ExecutePlaceNear(ActionAttempt attempt)
    {
        var parameters = ActionParameterSerializer.Deserialize<PlaceNearActionParams>(attempt.Parameters);
        if (parameters == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Invalid place_near parameters");
        }

        if (_referenceResolver == null)
        {
            return ActionExecutionResult.Failed(ActionFailureReason.Unspecified, "Reference resolver not configured");
        }

        return _interactionHandler.ExecutePlaceNear(
            attempt,
            EntityManager,
            parameters.TargetRef,
            reference => _referenceResolver(attempt, reference),
            parameters.TargetLocation?.X,
            parameters.TargetLocation?.Y);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Supporting Types
// ═══════════════════════════════════════════════════════════════════════════

internal sealed class BodyAuthorityData
{
    public required BodyId BodyId { get; init; }
    public required AgentId AgentId { get; set; }
    public required ConnectionId ConnectionId { get; set; }
    public required uint Generation { get; set; }
    public required ulong GrantedAtTick { get; set; }
}

internal readonly struct AuthorityValidationResult
{
    public bool IsValid { get; init; }
    public ActionRejectionReason? RejectionReason { get; init; }
    public string? Detail { get; init; }

    public static AuthorityValidationResult Valid() => new() { IsValid = true };
    public static AuthorityValidationResult Invalid(ActionRejectionReason reason, string? detail = null) =>
        new() { IsValid = false, RejectionReason = reason, Detail = detail };
}

internal readonly struct CapabilityValidationResult
{
    public bool IsValid { get; init; }
    public ActionRejectionReason Reason { get; init; }
    public string? Detail { get; init; }

    public static CapabilityValidationResult Valid() => new() { IsValid = true };
    public static CapabilityValidationResult Invalid(ActionRejectionReason reason, string? detail = null) =>
        new() { IsValid = false, Reason = reason, Detail = detail };
}

public readonly struct ActionProposalResult
{
    public bool IsAccepted { get; init; }
    public ActionRejectionReason? RejectionReason { get; init; }
    public string? Detail { get; init; }

    public static ActionProposalResult Accepted() => new() { IsAccepted = true };
    public static ActionProposalResult Rejected(ActionRejectionReason reason, string? detail = null) =>
        new() { IsAccepted = false, RejectionReason = reason, Detail = detail };
}

public readonly struct ActionExecutionResult
{
    public bool IsSuccess { get; init; }
    public bool IsStarted { get; init; }
    public bool IsNotFound { get; init; }
    public ActionFailureReason? FailureReason { get; init; }
    public ActionResultData? ResultData { get; init; }
    public string? Detail { get; init; }

    public static ActionExecutionResult Started() => new() { IsSuccess = true, IsStarted = true };
    public static ActionExecutionResult Completed(ActionResultData? data) =>
        new() { IsSuccess = true, ResultData = data };
    public static ActionExecutionResult Failed(ActionFailureReason reason, string? detail = null) =>
        new() { IsSuccess = false, FailureReason = reason, Detail = detail };
    public static ActionExecutionResult NotFound() => new() { IsSuccess = false, IsNotFound = true };
    public static ActionExecutionResult InvalidState(ActionState state) =>
        new() { IsSuccess = false, Detail = $"Invalid state: {state}" };
    public static ActionExecutionResult UnsupportedCapability(ActionCapability capability) =>
        new() { IsSuccess = false, Detail = $"Unsupported capability: {capability}" };
}