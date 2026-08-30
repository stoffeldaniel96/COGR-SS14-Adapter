using System.Text;
using System.Text.Json;
using COGR.Core.Identifiers;
using Content.Server.COGR.Systems;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Proto = COGR.Transport.Grpc.Protocol.V1;

namespace Content.Server.COGR.Commands;

/// <summary>
/// Seeds one generic semantic desired-state task through the live COGR runtime duplex path.
/// Station validates only live connection/body authority and forwards semantic concept identities;
/// it does not choose a target, action capability, procedure, or rendered response.
/// </summary>
public sealed class COGRRuntimeDesiredStateCommand : IConsoleCommand
{
    private const int MaximumConceptIdCharacters = 256;
    private const int MaximumDisplayedResponseCharacters = 8192;
    private const string RuntimeCommand = "cogr.vs1f.desired_state";

    public string Command => "cogr_runtime_desired_state";
    public string Description => "Seeds a generic semantic desired-state task for one COGR-controlled agent.";
    public string Help =>
        "cogr_runtime_desired_state <agent-id> <target-concept-id> <desired-state-concept-id>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3)
        {
            shell.WriteError(Help);
            return;
        }

        if (!Guid.TryParse(args[0], out var agentGuid) || agentGuid == Guid.Empty)
        {
            shell.WriteError("agent-id must be an assigned UUID.");
            return;
        }

        if (!IsBoundedConceptId(args[1]))
        {
            shell.WriteError($"target-concept-id must be non-empty and no more than {MaximumConceptIdCharacters} characters.");
            return;
        }

        if (!IsBoundedConceptId(args[2]))
        {
            shell.WriteError($"desired-state-concept-id must be non-empty and no more than {MaximumConceptIdCharacters} characters.");
            return;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        var adapter = systems.GetEntitySystem<COGRAdapterSystem>();
        var authority = systems.GetEntitySystem<COGRBodyAuthorityCoordinatorSystem>();
        if (adapter.Connection is not { IsConnected: true } connection)
        {
            shell.WriteError("COGR runtime is not connected.");
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

        var parameters = JsonSerializer.SerializeToUtf8Bytes(new
        {
            agentId = agentId.ToString(),
            targetConceptId = args[1],
            desiredStateConceptId = args[2],
        });

        var correlationId = Guid.Empty;
        Action<Proto.AdministrativeResponse> responseHandler = null!;
        responseHandler = response =>
        {
            if (!Guid.TryParse(response.CorrelationId?.Value, out var responseCorrelation) ||
                responseCorrelation != correlationId)
            {
                return;
            }

            connection.AdministrativeResponseReceived -= responseHandler;
            var payload = Encoding.UTF8.GetString(response.Data.Span);
            if (payload.Length > MaximumDisplayedResponseCharacters)
            {
                payload = payload[..MaximumDisplayedResponseCharacters] +
                          "\n[response truncated by Station diagnostic console]";
            }

            if (response.Success)
            {
                shell.WriteLine(string.IsNullOrWhiteSpace(payload)
                    ? "COGR desired-state task was accepted without response data."
                    : payload);
            }
            else
            {
                shell.WriteError(string.IsNullOrWhiteSpace(response.Error)
                    ? "COGR desired-state task was rejected."
                    : response.Error);
            }
        };

        connection.AdministrativeResponseReceived += responseHandler;
        try
        {
            correlationId = connection.SendAdministrativeCommand(RuntimeCommand, parameters);
        }
        catch (Exception ex)
        {
            connection.AdministrativeResponseReceived -= responseHandler;
            shell.WriteError($"Failed to queue COGR desired-state task: {ex.Message}");
            return;
        }

        shell.WriteLine(
            $"Queued desired-state task for agent {agentId}; target={args[1]}, desired={args[2]}, " +
            $"correlation {correlationId:D}. Runtime acceptance/status will print here when received.");
    }

    private static bool IsBoundedConceptId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumConceptIdCharacters;
}
