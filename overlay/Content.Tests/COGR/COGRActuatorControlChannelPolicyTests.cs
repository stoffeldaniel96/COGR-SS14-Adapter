using System;
using COGR.Core.Actions;
using COGR.Core.Identifiers;
using COGR.Core.Sequences;
using COGR.Core.Time;
using Content.Server.COGR.Actions;
using NUnit.Framework;

namespace Content.Tests.COGR;

[TestFixture]
public sealed class COGRActuatorControlChannelPolicyTests
{
    [Test]
    public void LocomotionAndBodyOrientation_AreIndependentPhysicalChannels()
    {
        var locomotion = COGRActuatorControlChannelPolicy.GetClaims(
            ActionCapability.MovementSteerToBodyRelativePoint);
        var orientation = COGRActuatorControlChannelPolicy.GetClaims(
            ActionCapability.MovementMaintainOrientationToReference);

        Assert.That(locomotion, Is.EqualTo(COGRPhysicalControlChannel.Locomotion));
        Assert.That(orientation, Is.EqualTo(COGRPhysicalControlChannel.BodyOrientation));
        Assert.That(locomotion & orientation, Is.EqualTo(COGRPhysicalControlChannel.None));
    }

    [Test]
    public void CompositeSpatialRelation_ConservativelyClaimsLocomotionAndBodyOrientation()
    {
        var claims = COGRActuatorControlChannelPolicy.GetClaims(
            ActionCapability.MovementEstablishSpatialRelation);

        Assert.That(claims.HasFlag(COGRPhysicalControlChannel.Locomotion), Is.True);
        Assert.That(claims.HasFlag(COGRPhysicalControlChannel.BodyOrientation), Is.True);
    }

    [Test]
    public void Registry_AllowsLocomotionWhileSustainedBodyOrientationIsActive()
    {
        var registry = new COGRActionRegistry();
        var body = BodyId.NewId();
        var orientation = CreateAttempt(body, ActionCapability.MovementMaintainOrientationToReference);
        registry.Register(orientation);
        registry.UpdateState(orientation.ProposalId, ActionState.Started, new SimTick(10));

        var conflict = registry.GetConflictingAction(
            body,
            ActionCapability.MovementSteerToBodyRelativePoint);

        Assert.That(conflict, Is.Null);
    }

    [Test]
    public void Registry_AllowsBodyTurnWhileLocomotionIsActive()
    {
        var registry = new COGRActionRegistry();
        var body = BodyId.NewId();
        var locomotion = CreateAttempt(body, ActionCapability.MovementSteerRelative);
        registry.Register(locomotion);
        registry.UpdateState(locomotion.ProposalId, ActionState.Progressing, new SimTick(10));

        var conflict = registry.GetConflictingAction(body, ActionCapability.MovementTurn);

        Assert.That(conflict, Is.Null);
    }

    [Test]
    public void Registry_RejectsCompetingLocomotionClaims()
    {
        var registry = new COGRActionRegistry();
        var body = BodyId.NewId();
        var active = CreateAttempt(body, ActionCapability.MovementSteerRelative);
        registry.Register(active);
        registry.UpdateState(active.ProposalId, ActionState.Progressing, new SimTick(10));

        var conflict = registry.GetConflictingAction(
            body,
            ActionCapability.MovementSteerToBodyRelativePoint);

        Assert.That(conflict?.ProposalId, Is.EqualTo(active.ProposalId));
    }

    [Test]
    public void Registry_RejectsCompetingBodyOrientationClaims()
    {
        var registry = new COGRActionRegistry();
        var body = BodyId.NewId();
        var active = CreateAttempt(body, ActionCapability.MovementMaintainOrientationToReference);
        registry.Register(active);
        registry.UpdateState(active.ProposalId, ActionState.Progressing, new SimTick(10));

        var conflict = registry.GetConflictingAction(body, ActionCapability.MovementTurn);

        Assert.That(conflict?.ProposalId, Is.EqualTo(active.ProposalId));
    }

    [Test]
    public void Registry_PreservesNonMovementCategoryConflictsThroughPhysicalChannels()
    {
        var registry = new COGRActionRegistry();
        var body = BodyId.NewId();
        var active = CreateAttempt(body, ActionCapability.InteractionOpen);
        registry.Register(active);
        registry.UpdateState(active.ProposalId, ActionState.Started, new SimTick(10));

        var conflict = registry.GetConflictingAction(body, ActionCapability.InteractionClose);

        Assert.That(conflict?.ProposalId, Is.EqualTo(active.ProposalId));
    }

    private static ActionAttempt CreateAttempt(BodyId body, ActionCapability capability)
    {
        var agent = AgentId.NewId();
        return new ActionAttempt
        {
            ProposalId = ActionProposalId.NewId(),
            AgentId = agent,
            BodyId = body,
            AuthorityLease = BodyAuthorityLease.Create(body, agent, ConnectionId.NewId()),
            CausalTraceId = CausalTraceId.NewId(),
            ProposedAtTick = new SimTick(1),
            RuntimeSequence = new RuntimeSequence(1),
            Capability = capability,
            Parameters = ReadOnlyMemory<byte>.Empty,
            ParameterFormat = "json",
        };
    }
}
