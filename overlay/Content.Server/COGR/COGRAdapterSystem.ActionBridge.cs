using System.Threading.Channels;
using COGR.Contracts.Messages;
using COGR.Core.Actions;
using COGR.Core.Time;
using COGR.SS14Bridge;
using Proto = COGR.Transport.Grpc.Protocol.V1;

namespace Content.Server.COGR;

public sealed partial class COGRAdapterSystem
{
    private Channel<EnvironmentMessage>? _actionBridgeOutgoingMessages;

    /// <summary>
    /// Gets the contract messages emitted by the shared action bridge.
    /// </summary>
    /// <remarks>
    /// The connection transport preserves world, connection, tick, proposal, correlation,
    /// and result identity. It assigns the final connection-global source sequence and latest
    /// runtime acknowledgement immediately before wire mapping.
    /// </remarks>
    public ChannelReader<EnvironmentMessage>? ActionBridgeOutgoingMessages =>
        _actionBridgeOutgoingMessages?.Reader;

    /// <summary>
    /// Routes a decoded runtime action proposal through the configured shared bridge. Qualitative steering remains
    /// qualitative across this boundary; Station's native locomotor executor realizes the bearing without fabricating a
    /// destination coordinate or rewriting the requested capability.
    /// </summary>
    public void HandleRuntimeActionProposal(ActionProposalMessage proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[PROMPTED] action.proposal agent={0} proposal={1}",
                proposal.AgentId,
                proposal.ProposalId);
        }

        if (_actionBridge == null)
        {
            _sawmill.Warning(
                "Action proposal rejected: proposal={0} reason=bridge_unavailable",
                proposal.ProposalId);
            return;
        }

        _actionBridge.HandleActionProposal(proposal);
    }

    /// <summary>
    /// Cancels an accepted active action identified by the runtime.
    /// </summary>
    public void HandleRuntimeActionCancellation(ActionCancellationMessage cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        if (COGRAdapterTrace.Enabled)
        {
            _sawmill.Info(
                "[PROMPTED] action.cancel proposal={0}",
                cancellation.ProposalId);
        }

        if (_actionExecutor == null || _actionBridge == null)
        {
            _sawmill.Warning(
                "Action cancellation ignored: proposal={0} reason=routing_unavailable",
                cancellation.ProposalId);
            return;
        }

        var active = _actionExecutor.ActionRegistry.GetAction(cancellation.ProposalId);
        if (active == null)
        {
            _sawmill.Warning(
                "Action cancellation ignored: proposal={0} reason=unknown_or_terminal",
                cancellation.ProposalId);
            return;
        }

        var tick = new SimTick((ulong)_gameTiming.CurTick.Value);
        _actionExecutor.CleanupActionTracking(active.ProposalId, active.BodyId);
        _actionExecutor.ActionRegistry.UpdateState(active.ProposalId, ActionState.Cancelled, tick);
        _actionExecutor.ActionRegistry.Remove(active.ProposalId);
        _actionBridge.OnActionTerminalResult(ActionResult.Cancelled(
            active.ProposalId,
            tick,
            cancellation.Reason ?? "Cancelled by runtime"));
    }

    internal void AttachActionBridge(
        ActionBridge bridge,
        Channel<EnvironmentMessage> outgoingMessages)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(outgoingMessages);

        _actionBridge = bridge;
        _actionBridgeOutgoingMessages = outgoingMessages;

        if (Connection != null)
        {
            Connection.ActionProposalReceived -= HandleRuntimeActionProposal;
            Connection.ActionCancellationReceived -= HandleRuntimeActionCancellation;
            Connection.AdministrativeResponseReceived -= OnAdministrativeResponseReceived;
            Connection.ActionProposalReceived += HandleRuntimeActionProposal;
            Connection.ActionCancellationReceived += HandleRuntimeActionCancellation;
            Connection.AdministrativeResponseReceived += OnAdministrativeResponseReceived;
            Connection.AttachBridgeMessages(outgoingMessages.Reader);
        }
    }

    internal void DetachActionBridge(ActionBridge bridge)
    {
        if (!ReferenceEquals(_actionBridge, bridge))
            return;

        if (Connection != null && _actionBridgeOutgoingMessages != null)
        {
            Connection.DetachBridgeMessages(_actionBridgeOutgoingMessages.Reader);
            Connection.ActionProposalReceived -= HandleRuntimeActionProposal;
            Connection.ActionCancellationReceived -= HandleRuntimeActionCancellation;
            Connection.AdministrativeResponseReceived -= OnAdministrativeResponseReceived;
        }

        _actionBridge = null;
        _actionBridgeOutgoingMessages = null;
    }

    private void OnAdministrativeResponseReceived(Proto.AdministrativeResponse response)
    {
        if (!COGRAdapterTrace.Enabled)
            return;

        var detail = response.Success
            ? (response.Data.IsEmpty ? "completed" : response.Data.ToStringUtf8())
            : response.Error;
        _sawmill.Info(
            "[PROMPTED] admin.response correlation={0} success={1} detail={2}",
            response.CorrelationId?.Value ?? "uncorrelated",
            response.Success,
            detail);
    }
}
