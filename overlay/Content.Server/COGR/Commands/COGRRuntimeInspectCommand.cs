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
/// Sends a bounded self-anchored visual inspection through the complete runtime duplex path.
/// Body identity and authority generation are resolved from Station rather than supplied by the operator.
/// </summary>
public sealed class COGRRuntimeInspectCommand : IConsoleCommand
{
    private const int MaximumDisplayedResponseCharacters = 32768;

    public string Command => "cogr_runtime_inspect";
    public string Description => "Requests bounded visual perception for one COGR-controlled agent.";
    public string Help => "cogr_runtime_inspect <agent-id> [max-distance] [max-observations]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 3)
        {
            shell.WriteError(Help);
            return;
        }

        if (!Guid.TryParse(args[0], out var agentGuid) || agentGuid == Guid.Empty)
        {
            shell.WriteError("agent-id must be an assigned UUID.");
            return;
        }

        var maxDistance = 6.0;
        if (args.Length >= 2 &&
            (!double.TryParse(args[1], out maxDistance) || maxDistance is <= 0 or > 32))
        {
            shell.WriteError("max-distance must be greater than zero and no more than 32.");
            return;
        }

        var maxObservations = 16;
        if (args.Length >= 3 &&
            (!int.TryParse(args[2], out maxObservations) || maxObservations is < 1 or > 128))
        {
            shell.WriteError("max-observations must be between 1 and 128.");
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
            bodyId = lease.Value.BodyId.ToString(),
            bodyGeneration = lease.Value.Generation,
            maxEntitiesConsidered = 64,
            maxObservationsReturned = maxObservations,
            maxDistance,
            maxTraversalDepth = 1,
            maxProcessingTimeMs = 20,
            supportContinuation = false,
            searchConceptHints = new[] { "door", "handheld_tool" },
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
                    ? "COGR inspection completed without response data."
                    : payload);
            }
            else
            {
                shell.WriteError(string.IsNullOrWhiteSpace(response.Error)
                    ? "COGR inspection failed."
                    : response.Error);
            }
        };

        connection.AdministrativeResponseReceived += responseHandler;
        try
        {
            correlationId = connection.SendAdministrativeCommand(
                "cogr.f3.inspect",
                parameters);
        }
        catch (Exception ex)
        {
            connection.AdministrativeResponseReceived -= responseHandler;
            shell.WriteError($"Failed to queue COGR inspection: {ex.Message}");
            return;
        }

        shell.WriteLine(
            $"Queued bounded perception for agent {agentId}, body {lease.Value.BodyId}, " +
            $"generation {lease.Value.Generation}; correlation {correlationId:D}. " +
            "The structured response will print here when received.");
    }
}
