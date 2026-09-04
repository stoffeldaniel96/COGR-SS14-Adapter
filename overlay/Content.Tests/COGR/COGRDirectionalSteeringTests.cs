using System;
using System.Numerics;
using System.Reflection;
using COGR.Core.Actions.Parameters;
using COGR.Core.Perception;
using Content.Server.COGR.Actions;
using NUnit.Framework;

namespace Content.Tests.COGR;

[TestFixture]
[TestOf(typeof(COGRActionExecutor))]
public sealed class COGRDirectionalSteeringTests
{
    private static readonly Type ExecutorType = typeof(COGRActionExecutor);

    [Test]
    public void ContinuousPlanarDirection_RemainsContinuous()
    {
        var parameters = new SteerRelativeActionParams
        {
            Direction = new BodyRelativeDirectionVector(2d, 1d, 0d).Normalize(),
        };

        var (accepted, direction, mode) = Resolve(parameters);

        Assert.That(accepted, Is.True);
        Assert.That(mode, Is.EqualTo("continuous"));
        Assert.That(direction.X, Is.EqualTo((float)(2d / Math.Sqrt(5d))).Within(1e-5f));
        Assert.That(direction.Y, Is.EqualTo((float)(1d / Math.Sqrt(5d))).Within(1e-5f));
        Assert.That(direction.X, Is.Not.EqualTo(direction.Y));
    }

    [Test]
    public void OctantOnly_RemainsAvailableAsSimpleIntent()
    {
        var (accepted, direction, mode) = Resolve(new SteerRelativeActionParams
        {
            Bearing = BodyRelativeBearing.ForwardLeft,
        });

        Assert.That(accepted, Is.True);
        Assert.That(mode, Is.EqualTo("octant"));
        Assert.That(direction.X, Is.EqualTo(0.70710677f).Within(1e-6f));
        Assert.That(direction.Y, Is.EqualTo(0.70710677f).Within(1e-6f));
    }

    [Test]
    public void ContinuousWithinOctantEnvelope_PreservesContinuousDirection()
    {
        var continuous = new BodyRelativeDirectionVector(2d, 1d, 0d).Normalize();
        var (accepted, direction, mode) = Resolve(new SteerRelativeActionParams
        {
            Direction = continuous,
            Bearing = BodyRelativeBearing.ForwardLeft,
        });

        Assert.That(accepted, Is.True);
        Assert.That(mode, Is.EqualTo("continuous+octant"));
        Assert.That(direction.X, Is.EqualTo((float)continuous.Forward).Within(1e-5f));
        Assert.That(direction.Y, Is.EqualTo((float)continuous.Left).Within(1e-5f));
    }

    [Test]
    public void ContinuousOutsideOctantEnvelope_SnapsToCoarseIntent()
    {
        var (accepted, direction, mode) = Resolve(new SteerRelativeActionParams
        {
            Direction = new BodyRelativeDirectionVector(1d, 0d, 0d),
            Bearing = BodyRelativeBearing.Left,
        });

        Assert.That(accepted, Is.True);
        Assert.That(mode, Is.EqualTo("octant-snap"));
        Assert.That(direction.X, Is.EqualTo(0f).Within(1e-6f));
        Assert.That(direction.Y, Is.EqualTo(1f).Within(1e-6f));
    }

    [Test]
    public void NonPlanarContinuousDirection_FailsClosedInsteadOfDroppingVerticalIntent()
    {
        var (accepted, _, mode) = Resolve(new SteerRelativeActionParams
        {
            Direction = new BodyRelativeDirectionVector(1d, 0d, 1d).Normalize(),
        });

        Assert.That(accepted, Is.False);
        Assert.That(mode, Is.EqualTo("invalid"));
    }

    private static (bool Accepted, Vector2 Direction, string Mode) Resolve(SteerRelativeActionParams parameters)
    {
        var method = ExecutorType.GetMethod(
            "TryResolveOwnerRelativeSteeringDirection",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);

        object?[] args = [parameters, Vector2.Zero, string.Empty];
        var accepted = (bool)method!.Invoke(null, args)!;
        return (accepted, (Vector2)args[1]!, (string)args[2]!);
    }
}