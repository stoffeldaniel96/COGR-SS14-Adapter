using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Time;
using Content.Server.Administration;
using Content.Server.COGR.Systems;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Commands;

/// <summary>
/// Admin-only live F3 fixture command. Resolves one opaque environment reference under the
/// current Station body lease, then removes that observed target so the normal entity lifecycle
/// produces a typed reference invalidation.
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class COGRRemoveObservedTargetCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IEntitySystemManager _systems = default!;
    [Dependency] private IGameTiming _timing = default!;

    public string Command => "cogr_f3_remove_observed";
    public string Description =>
        "Removes a target through an opaque COGR reference to exercise F3 invalidation.";
    public string Help => "cogr_f3_remove_observed <agent-id> <environment-reference>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (!Guid.TryParse(args[0], out var agentGuid) || agentGuid == Guid.Empty)
        {
            shell.WriteError("agent-id must be an assigned UUID.");
            return;
        }

        if (!Guid.TryParse(args[1], out var referenceGuid) || referenceGuid == Guid.Empty)
        {
            shell.WriteError("environment-reference must be the UUID returned by cogr_runtime_inspect.");
            return;
        }

        var adapter = _systems.GetEntitySystem<COGRAdapterSystem>();
        var authority = _systems.GetEntitySystem<COGRBodyAuthorityCoordinatorSystem>();
        if (adapter.Connection is not { IsConnected: true } connection ||
            adapter.ReferenceRegistry == null ||
            connection.ConnectionId == Guid.Empty)
        {
            shell.WriteError("COGR runtime or Station reference storage is unavailable.");
            return;
        }

        var connectionId = ConnectionId.FromGuid(connection.ConnectionId);
        var agentId = AgentId.FromGuid(agentGuid);
        var lease = authority.ResolveBoundLease(agentId, connectionId);
        if (!lease.HasValue)
        {
            shell.WriteError("No unambiguous current body authority exists for this agent.");
            return;
        }

        var environmentReference = EnvironmentRef.FromGuid(referenceGuid);
        var target = adapter.ReferenceRegistry.TryResolve(
            environmentReference,
            new EnvironmentReferenceResolutionContext
            {
                ConnectionId = connectionId,
                CurrentTick = new SimTick((ulong)_timing.CurTick.Value),
                BodyId = lease.Value.BodyId,
                BodyGeneration = lease.Value.Generation,
            });

        if (!target.HasValue)
        {
            shell.WriteError(
                "The reference is stale, belongs to another connection/body generation, or no longer resolves.");
            return;
        }

        _entities.QueueDeleteEntity(target.Value);
        shell.WriteLine(
            $"Queued removal of the target addressed by {referenceGuid:D}; " +
            "Station should emit entity_terminated invalidation on the next lifecycle pass.");
    }
}
