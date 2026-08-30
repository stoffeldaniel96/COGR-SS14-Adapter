using Content.Server.COGR;
using NUnit.Framework;

namespace Content.Tests.COGR;

[TestFixture]
public sealed class COGRSpatialPolicyTests
{
    [Test]
    public void LocalPathfindingHorizon_MatchesPerceptionHorizon_WhileStepRemainsShort()
    {
        Assert.That(
            COGRSpatialPolicy.MaximumLocalPathfindingDistance,
            Is.EqualTo(COGRSpatialPolicy.DefaultVisualHorizon));
        Assert.That(COGRSpatialPolicy.MaximumLocalPathfindingDistance, Is.EqualTo(12.0f));
        Assert.That(COGRSpatialPolicy.MaximumStepDistance, Is.EqualTo(4.0f));
        Assert.That(
            COGRSpatialPolicy.MaximumStepDistance,
            Is.LessThan(COGRSpatialPolicy.MaximumLocalPathfindingDistance));
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
            Is.LessThan(COGRSpatialPolicy.MaximumLocalPathfindingDistance));
    }
}
