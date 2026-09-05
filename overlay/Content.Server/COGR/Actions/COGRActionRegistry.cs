using System.Linq;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Time;

namespace Content.Server.COGR.Actions;

/// <summary>
/// SS14 implementation of active action registry for F02 action lifecycle.
/// Tracks active actions, detects embodiment control conflicts, and enforces timeouts.
/// </summary>
public sealed class COGRActionRegistry : IActiveActionRegistry
{
    private readonly Dictionary<ActionProposalId, ActionAttempt> _actions = new();
    private readonly object _lock = new();

    public void Register(ActionAttempt attempt)
    {
        lock (_lock)
        {
            _actions[attempt.ProposalId] = attempt;
        }
    }

    public ActionAttempt? GetAction(ActionProposalId proposalId)
    {
        lock (_lock)
        {
            return _actions.TryGetValue(proposalId, out var attempt) ? attempt : null;
        }
    }

    public IEnumerable<ActionAttempt> GetActiveForAgent(AgentId agentId)
    {
        lock (_lock)
        {
            return _actions.Values
                .Where(a => a.AgentId == agentId && !a.State.IsTerminal())
                .ToList();
        }
    }

    public IEnumerable<ActionAttempt> GetActiveForBody(BodyId bodyId)
    {
        lock (_lock)
        {
            return _actions.Values
                .Where(a => a.BodyId == bodyId && !a.State.IsTerminal())
                .ToList();
        }
    }

    public IEnumerable<ActionAttempt> GetTimedOut(SimTick currentTick, uint defaultTimeoutMs, double msPerTick)
    {
        lock (_lock)
        {
            var timedOut = new List<ActionAttempt>();
            var currentTickValue = currentTick.Value;

            foreach (var attempt in _actions.Values.Where(a => !a.State.IsTerminal()))
            {
                if (attempt.TimeoutMs == 0 && attempt.Capability.HasSustainedLifetime())
                    continue;
                if (attempt.Capability == ActionCapability.MovementEstablishSpatialRelation
                    && ActionParameterSerializer.Deserialize<EstablishSpatialRelationParams>(attempt.Parameters)?.Maintain == true)
                {
                    continue;
                }

                var timeoutMs = attempt.TimeoutMs > 0 ? attempt.TimeoutMs : defaultTimeoutMs;
                var timeoutTicks = (ulong)(timeoutMs / msPerTick);
                var proposedTick = attempt.ProposedAtTick.Value;

                if (currentTickValue >= proposedTick + timeoutTicks)
                {
                    timedOut.Add(attempt);
                }
            }

            return timedOut;
        }
    }

    public ActionAttempt? GetConflictingAction(BodyId bodyId, ActionCapability capability)
    {
        lock (_lock)
        {
            var requestedClaims = COGRActuatorControlChannelPolicy.GetClaims(capability);
            if (requestedClaims == COGRPhysicalControlChannel.None)
                return null;

            return _actions.Values
                .Where(a => a.BodyId == bodyId
                            && !a.State.IsTerminal()
                            && (COGRActuatorControlChannelPolicy.GetClaims(a.Capability) & requestedClaims) != 0)
                .FirstOrDefault();
        }
    }

    public bool UpdateState(ActionProposalId proposalId, ActionState newState, SimTick tick)
    {
        lock (_lock)
        {
            if (_actions.TryGetValue(proposalId, out var attempt))
            {
                // Runtime proposal ticks and SS14 simulation ticks are separate clock domains.
                // Once Station accepts an action, all execution deadlines, movement timeout
                // checks, and stall detection must be anchored to the authoritative Station
                // tick. StartAction retrieves this normalized attempt before invoking handlers.
                var executionAnchorTick = newState == ActionState.Accepted
                    ? tick
                    : attempt.ProposedAtTick;

                var updated = attempt with
                {
                    State = newState,
                    StateChangedAtTick = tick,
                    ProposedAtTick = executionAnchorTick
                };
                _actions[proposalId] = updated;
                return true;
            }
            return false;
        }
    }

    public ActionAttempt? Remove(ActionProposalId proposalId)
    {
        lock (_lock)
        {
            if (_actions.TryGetValue(proposalId, out var attempt))
            {
                _actions.Remove(proposalId);
                return attempt;
            }
            return null;
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                return _actions.Values.Count(a => !a.State.IsTerminal());
            }
        }
    }

    public int GetActiveCountForAgent(AgentId agentId)
    {
        lock (_lock)
        {
            return _actions.Values.Count(a => a.AgentId == agentId && !a.State.IsTerminal());
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _actions.Clear();
        }
    }

    /// <summary>
    /// Gets all actions (for debugging/admin commands).
    /// </summary>
    public IEnumerable<ActionAttempt> GetAll()
    {
        lock (_lock)
        {
            return _actions.Values.ToList();
        }
    }
}
