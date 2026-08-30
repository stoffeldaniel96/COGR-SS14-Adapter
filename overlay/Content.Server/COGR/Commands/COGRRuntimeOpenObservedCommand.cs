using System.Text;
using System.Text.Json;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Proto = COGR.Transport.Grpc.Protocol.V1;

namespace Content.Server.COGR.Commands;

/// <summary>
/// Requests an interaction.open action from the runtime using an opaque reference
/// previously returned by cogr_runtime_inspect.
/// </summary>
public sealed class COGRRuntimeOpenObservedCommand : IConsoleCommand
{
    private const int MaximumDisplayedResponseCharacters = 8192;

    public string Command => "cogr_runtime_open_observed";
    public string Description =>
        "Requests that a COGR agent open a previously observed target through the runtime action path.";
    public string Help =>
        "cogr_runtime_open_observed <agent-id> <environment-reference> [force]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            shell.WriteError(Help);
            return;
        }

        if (!Guid.TryParse(args[0], out var agentId) || agentId == Guid.Empty)
        {
            shell.WriteError("agent-id must be an assigned UUID.");
            return;
        }

        if (!Guid.TryParse(args[1], out var environmentReference) ||
            environmentReference == Guid.Empty)
        {
            shell.WriteError("environment-reference must be an assigned opaque UUID.");
            return;
        }

        var force = false;
        if (args.Length == 3 && !bool.TryParse(args[2], out force))
        {
            shell.WriteError("force must be true or false.");
            return;
        }

        var systems = IoCManager.Resolve<IEntitySystemManager>();
        var adapter = systems.GetEntitySystem<COGRAdapterSystem>();
        if (adapter.Connection is not { IsConnected: true } connection)
        {
            shell.WriteError("COGR runtime is not connected.");
            return;
        }

        var parameters = JsonSerializer.SerializeToUtf8Bytes(new
        {
            agentId = agentId.ToString("D"),
            environmentReference = environmentReference.ToString("D"),
            force,
            priority = 100,
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
                    ? "COGR observed-target open completed."
                    : payload);
            }
            else
            {
                shell.WriteError(string.IsNullOrWhiteSpace(response.Error)
                    ? "COGR observed-target open failed."
                    : response.Error);
            }
        };

        connection.AdministrativeResponseReceived += responseHandler;
        try
        {
            correlationId = connection.SendAdministrativeCommand(
                "cogr.f3.open_observed",
                parameters);
        }
        catch (Exception ex)
        {
            connection.AdministrativeResponseReceived -= responseHandler;
            shell.WriteError($"Failed to queue observed-target open: {ex.Message}");
            return;
        }

        shell.WriteLine(
            $"Queued runtime-originated interaction.open for agent {agentId:D} using opaque reference " +
            $"{environmentReference:D}; correlation {correlationId:D}. " +
            "The authoritative terminal result will print here when received.");
    }
}
