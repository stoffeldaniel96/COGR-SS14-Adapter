using Content.Server.COGR;
using NUnit.Framework;

namespace Content.Tests.COGR;

[TestFixture]
public sealed class COGRSpatialPolicyTests
{
    [Test]
    public void LocalPathfindingAndDirectionalSteering_MatchPerceptionHorizon_WhileStepRemainsShort()
    {
        Assert.That(
            COGRSpatialPolicy.MaximumLocalPathfindingDistance,
            Is.EqualTo(COGRSpatialPolicy.DefaultVisualHorizon));
        Assert.That(
            COGRSpatialPolicy.MaximumDirectionalSteeringProgress,
            Is.EqualTo(COGRSpatialPolicy.DefaultVisualHorizon));
        Assert.That(COGRSpatialPolicy.MaximumLocalPathfindingDistance, Is.EqualTo(12.0f));
        Assert.That(COGRSpatialPolicy.MaximumStepDistance, Is.EqualTo(4.0f));
        Assert.That(
            COGRSpatialPolicy.MaximumStepDistance,
            Is.LessThan(COGRSpatialPolicy.MaximumDirectionalSteeringProgress));
    }

    [Test]
    public void LocalTravelBudget_RemainsBoundedByPathfindingHorizon()
    {
        Assert.That(
            COGRSpatialPolicy.GetMaximumLocalTravelDistance(1.0f),
            Is.LessThanOrEqualTo(COGRSpatialPolicy.MaximumLocalPathfindingDistance));
        Assert.That(
            COGRSpatialPolicy.GetMaximumLocalTravelDistance(
                COGRSpatialPolicy.MaximumLocalPathfindingDistance),
            Is.EqualTo(COGRSpatialPolicy.MaximumLocalPathfindingDistance));
        Assert.That(
            COGRSpatialPolicy.BlindContinuationDistance,
            Is.EqualTo(COGRSpatialPolicy.DefaultVisualHorizon));
    }
}
