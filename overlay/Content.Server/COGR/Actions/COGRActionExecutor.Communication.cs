using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using Content.Server.Chat.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.COGR.Components;
using Robust.Shared.Configuration;

namespace Content.Server.COGR.Actions;

public sealed partial class COGRActionExecutor
{
    [Dependency] private ChatSystem _chatSystem = default!;
    [Dependency] private ActionBlockerSystem _actionBlockerSystem = default!;
    [Dependency] private IConfigurationManager _configurationManager = default!;

    private CapabilityValidationResult ValidateSpeakLocalParams(ReadOnlyMemory<byte> parameters)
    {
        var speech = ActionParameterSerializer.Deserialize<SpeakLocalActionParams>(parameters);
        if (speech == null || string.IsNullOrWhiteSpace(speech.Text))
        {
            return CapabilityValidationResult.Invalid(
                ActionRejectionReason.InvalidParameters,
                "Local speech requires non-blank rendered text");
        }

        if (speech.Text.Length > CommunicationActionParameterLimits.MaximumSpeakLocalTextLength)
        {
            return CapabilityValidationResult.Invalid(
                ActionRejectionReason.InvalidParameters,
                $"Local speech exceeds the COGR hard limit of {CommunicationActionParameterLimits.MaximumSpeakLocalTextLength} UTF-16 code units");
        }

        var stationLimit = _configurationManager.GetCVar(CCVars.ChatMaxMessageLength);
        if (speech.Text.Length > stationLimit)
        {
            return CapabilityValidationResult.Invalid(
                ActionRejectionReason.InvalidParameters,
                $"Local speech exceeds the Station chat limit of {stationLimit} UTF-16 code units");
        }

        return CapabilityValidationResult.Valid();
    }

    private ActionExecutionResult ExecuteSpeakLocal(ActionAttempt attempt)
    {
        var speech = ActionParameterSerializer.Deserialize<SpeakLocalActionParams>(attempt.Parameters);
        if (speech == null || string.IsNullOrWhiteSpace(speech.Text))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.Unspecified,
                "Invalid local speech parameters");
        }

        var speaker = ResolveControlledBodyEntity(attempt);
        if (speaker == null)
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.BodyReplaced,
                "The authoritative COGR body is not represented by an active controlled entity");
        }

        if (!_actionBlockerSystem.CanSpeak(speaker.Value))
        {
            return ActionExecutionResult.Failed(
                ActionFailureReason.BodyBecameIncapacitated,
                "The authoritative COGR body is currently unable to speak");
        }

        _chatSystem.TrySendInGameICMessage(
            speaker.Value,
            speech.Text,
            InGameICChatType.Speak,
            ChatTransmitRange.Normal,
            hideLog: false,
            shell: null,
            player: null,
            nameOverride: null,
            checkRadioPrefix: false,
            ignoreActionBlocker: false);

        return ActionExecutionResult.Completed(null);
    }

    private EntityUid? ResolveControlledBodyEntity(ActionAttempt attempt)
    {
        var query = AllEntityQuery<COGRControlledComponent>();
        while (query.MoveNext(out var uid, out var controlled))
        {
            if (controlled.BodyId != attempt.BodyId.ToGuid()
                || controlled.AgentId != attempt.AgentId.ToGuid()
                || !controlled.IsActive)
            {
                continue;
            }

            return uid;
        }

        return null;
    }
}
