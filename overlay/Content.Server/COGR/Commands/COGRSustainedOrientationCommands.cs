using System.Linq;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Sequences;
using COGR.Core.Time;
using Content.Server.Administration;
using Content.Server.COGR.Actions;
using Content.Server.COGR.Systems;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Commands;

/// <summary>
/// Starts the generic sustained-orientation capability against one exact currently perceived opaque referent.
/// This command intentionally requires live Station authority and semantic-replica membership; it does not
/// manufacture references, body leases, or adapter-private entity targets.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRMaintainOrientationCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override string Command => "cogr_maintain_orientation";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError("Usage: cogr_maintain_orientation <agent-id> <environment-reference>");
            return;
        }

        if (!Guid.TryParse(args[0], out var agentGuid) || agentGuid == Guid.Empty)
        {
            shell.WriteError($"Invalid agent UUID: {args[0]}");
            return;
        }

        if (!Guid.TryParse(args[1], out var referenceGuid) || referenceGuid == Guid.Empty)
        {
            shell.WriteError($"Invalid environment-reference UUID: {args[1]}");
            return;
        }

        var agentId = AgentId.FromGuid(agentGuid);
        var targetRef = EnvironmentRef.FromGuid(referenceGuid);
        var authority = _entityManager.System<COGRBodyAuthorityCoordinatorSystem>();
        var executor = _entityManager.System<COGRActionExecutor>();
        var replica = _entityManager.System<COGRSemanticReplicaSystem>();

        if (!authority.BoundConnection.HasValue)
        {
            shell.WriteError("No live COGR connection currently owns Station body authority.");
            return;
        }

        var lease = authority.ResolveBoundLease(agentId, authority.BoundConnection.Value);
        if (!lease.HasValue)
        {
            shell.WriteError("No unique current Station body authority exists for that Coggent.");
            return;
        }

        var currentTick = new SimTick((ulong)_timing.CurTick.Value);
        var attempt = new ActionAttempt
        {
            ProposalId = ActionProposalId.NewId(),
            AgentId = agentId,
            BodyId = lease.Value.BodyId,
            AuthorityLease = lease.Value,
            CausalTraceId = CausalTraceId.NewId(),
            ProposedAtTick = currentTick,
            RuntimeSequence = new RuntimeSequence(1),
            Capability = ActionCapability.MovementMaintainOrientationToReference,
            Parameters = ActionParameterSerializer.Serialize(new MaintainOrientationToReferenceParams
            {
                TargetRef = targetRef,
            }),
            ParameterFormat = "json",
            TimeoutMs = 0,
        };

        if (!replica.IsReferenceCurrentlyObserved(attempt, targetRef))
        {
            shell.WriteError("Target reference is not present in this Coggent's exact current semantic replica.");
            return;
        }

        var proposal = executor.ProposeAction(attempt);
        shell.WriteLine($"ProposalId: {attempt.ProposalId}");
        shell.WriteLine($"Disposition: {(proposal.IsAccepted ? "Accepted" : "Rejected")}");
        if (!proposal.IsAccepted)
        {
            shell.WriteLine($"Reason: {proposal.RejectionReason} - {proposal.Detail}");
            return;
        }

        var execution = executor.StartAction(attempt.ProposalId);
        if (!execution.IsSuccess)
        {
            shell.WriteLine($"Execution failed: {execution.FailureReason} - {execution.Detail}");
            return;
        }

        shell.WriteLine(execution.IsStarted
            ? "Sustained orientation started. The body will face the target while it remains currently perceived."
            : $"Orientation completed immediately: {execution.Detail}");
    }
}

/// <summary>
/// Lists active sustained-orientation actions for one Coggent so live acceptance can distinguish
/// visual facing from an action that has already terminated.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGROrientationStatusCommand : LocalizedCommands
{
    [Dependency] private IEntityManager _entityManager = default!;

    public override string Command => "cogr_orientation_status";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError("Usage: cogr_orientation_status <agent-id>");
            return;
        }

        if (!Guid.TryParse(args[0], out var agentGuid) || agentGuid == Guid.Empty)
        {
            shell.WriteError($"Invalid agent UUID: {args[0]}");
            return;
        }

        var agentId = AgentId.FromGuid(agentGuid);
        var executor = _entityManager.System<COGRActionExecutor>();
        var orientations = executor.ActionRegistry
            .GetActiveForAgent(agentId)
            .Where(static action => action.Capability == ActionCapability.MovementMaintainOrientationToReference)
            .OrderBy(static action => action.ProposalId)
            .ToArray();

        shell.WriteLine($"Active sustained orientations for {agentGuid:D}: {orientations.Length}");
        foreach (var orientation in orientations)
        {
            var parameters = ActionParameterSerializer.Deserialize<MaintainOrientationToReferenceParams>(orientation.Parameters);
            shell.WriteLine(
                $"  - {orientation.ProposalId}: state={orientation.State}, target={parameters?.TargetRef.ToString() ?? "<invalid>"}");
        }
    }
}
