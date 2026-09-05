using COGR.Core.Actions;

namespace Content.Server.COGR.Actions;

/// <summary>
/// Physical control coordinates claimed by one adapter realization.
/// These are embodiment/execution channels, not cognitive action categories or semantic ontology.
/// </summary>
[Flags]
public enum COGRPhysicalControlChannel
{
    None = 0,
    Locomotion = 1 << 0,
    BodyOrientation = 1 << 1,
    Attention = 1 << 2,
    Communication = 1 << 3,
    Interaction = 1 << 4,
    Manipulation = 1 << 5,
}

/// <summary>
/// SS14-specific physical control-channel policy.
/// The Runtime may request independent cognitive actuator coordinates concurrently; this policy decides only whether
/// this embodiment can realize their current host controls at the same time.
/// </summary>
public static class COGRActuatorControlChannelPolicy
{
    public static COGRPhysicalControlChannel GetClaims(ActionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return GetClaims(attempt.Capability);
    }

    public static COGRPhysicalControlChannel GetClaims(ActionCapability capability) => capability switch
    {
        ActionCapability.MovementStep
            or ActionCapability.MovementStop
            or ActionCapability.MovementMoveToLocation
            or ActionCapability.MovementSteerRelative
            or ActionCapability.MovementSteerToBodyRelativePoint
            => COGRPhysicalControlChannel.Locomotion,

        // This legacy/composite primitive can both locomote and apply terminal body orientation.
        // Until its parameter-sensitive terminal preference is split into a separate orientation commitment,
        // conservatively reserve both physical coordinates rather than allowing an orientation race at completion.
        ActionCapability.MovementEstablishSpatialRelation
            => COGRPhysicalControlChannel.Locomotion | COGRPhysicalControlChannel.BodyOrientation,

        ActionCapability.MovementTurn
            or ActionCapability.MovementMaintainOrientationToReference
            => COGRPhysicalControlChannel.BodyOrientation,

        ActionCapability.AttentionOrientToReference
            or ActionCapability.AttentionOrientToLocation
            or ActionCapability.AttentionInspectRegion
            => COGRPhysicalControlChannel.Attention,

        ActionCapability.CommunicationSpeakLocal
            => COGRPhysicalControlChannel.Communication,

        ActionCapability.InteractionOpen
            or ActionCapability.InteractionClose
            or ActionCapability.InteractionIngest
            => COGRPhysicalControlChannel.Interaction,

        >= ActionCapability.ManipulationAcquire and <= ActionCapability.ManipulationApply
            => COGRPhysicalControlChannel.Manipulation,

        _ => COGRPhysicalControlChannel.None,
    };

    public static bool Claims(ActionAttempt attempt, COGRPhysicalControlChannel channel) =>
        (GetClaims(attempt) & channel) != 0;

    public static ActionAttempt? FindConflictingAction(
        IEnumerable<ActionAttempt> activeActions,
        ActionAttempt proposed)
    {
        ArgumentNullException.ThrowIfNull(activeActions);
        ArgumentNullException.ThrowIfNull(proposed);

        var proposedClaims = GetClaims(proposed);
        if (proposedClaims == COGRPhysicalControlChannel.None)
            return null;

        return activeActions.FirstOrDefault(active =>
            active.ProposalId != proposed.ProposalId
            && !active.State.IsTerminal()
            && (GetClaims(active) & proposedClaims) != 0);
    }
}
