using System;
using System.Numerics;
using System.Reflection;
using COGR.Core.Actions.Parameters;
using Content.Server.COGR.Actions;
using Content.Server.NPC.Components;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.COGR;

[TestFixture]
[TestOf(typeof(COGRActionExecutor))]
public sealed class COGRProjectedObjectiveSteeringTests
{
    private static readonly Type ExecutorType = typeof(COGRActionExecutor);

    [Test]
    public void PlanarObjective_UsesInverseEmbodimentCalibrationWithoutOctantSnap()
    {
        var resolve = RequireStaticMethod("TryResolvePlanarObjectiveNativeOffset");
        object?[] args =
        [
            new BodyRelativePointOffset(1d, 0.5d, 0d),
            Vector2.Zero,
            null,
        ];

        var accepted = (bool)resolve.Invoke(null, args)!;

        Assert.That(accepted, Is.True);
        var native = (Vector2)args[1]!;
        Assert.That(native.X, Is.EqualTo(0.70f).Within(0.00001f));
        Assert.That(native.Y, Is.EqualTo(0.35f).Within(0.00001f));
        Assert.That(args[2], Is.Null);
    }

    [Test]
    public void CurrentBodyRotation_ProjectsObjectiveIntoCurrentParentFrame()
    {
        var rotate = RequireStaticMethod("OwnerRelativeObjectiveToParentOffset");
        var projected = (Vector2)rotate.Invoke(
            null,
            [new Vector2(0.70f, 0f), new Angle(Math.PI / 2d)])!;

        Assert.That(projected.X, Is.EqualTo(0f).Within(0.00001f));
        Assert.That(projected.Y, Is.EqualTo(0.70f).Within(0.00001f));
    }

    [Test]
    public void ProjectedObjectiveArrivalTolerance_UsesNativeSteeringRange()
    {
        var resolve = RequireStaticMethod("TryResolveProjectedObjectiveArrivalTolerance");
        var nativeRange = new NPCSteeringComponent().Range;
        object?[] args =
        [
            nativeRange,
            0f,
        ];

        var accepted = (bool)resolve.Invoke(null, args)!;

        Assert.That(accepted, Is.True);
        Assert.That((float)args[1]!, Is.EqualTo(nativeRange));
        Assert.That(nativeRange, Is.EqualTo(0.20f));
    }

    [TestCase(0f)]
    [TestCase(-0.1f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void ProjectedObjectiveArrivalTolerance_RejectsInvalidNativeRange(float nativeRange)
    {
        var resolve = RequireStaticMethod("TryResolveProjectedObjectiveArrivalTolerance");
        object?[] args =
        [
            nativeRange,
            0f,
        ];

        var accepted = (bool)resolve.Invoke(null, args)!;

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void VerticalObjective_FailsClosedInsteadOfDroppingUpComponent()
    {
        var resolve = RequireStaticMethod("TryResolvePlanarObjectiveNativeOffset");
        object?[] args =
        [
            new BodyRelativePointOffset(1d, 0d, 0.1d),
            Vector2.Zero,
            null,
        ];

        var accepted = (bool)resolve.Invoke(null, args)!;

        Assert.That(accepted, Is.False);
        Assert.That((Vector2)args[1]!, Is.EqualTo(Vector2.Zero));
        Assert.That(args[2]?.ToString(), Does.Contain("vertical projected objective"));
    }

    [Test]
    public void ObjectiveBeyondNativeLocalPathHorizon_IsRejectedRatherThanSilentlySegmented()
    {
        var resolve = RequireStaticMethod("TryResolvePlanarObjectiveNativeOffset");
        object?[] args =
        [
            new BodyRelativePointOffset(18d, 0d, 0d),
            Vector2.Zero,
            null,
        ];

        var accepted = (bool)resolve.Invoke(null, args)!;

        Assert.That(accepted, Is.False);
        Assert.That(args[2]?.ToString(), Does.Contain("bounded native pathfinding horizon"));
    }

    [Test]
    public void ZeroObjective_IsRejectedRatherThanBecomingNoOpMovement()
    {
        var resolve = RequireStaticMethod("TryResolvePlanarObjectiveNativeOffset");
        object?[] args =
        [
            new BodyRelativePointOffset(0d, 0d, 0d),
            Vector2.Zero,
            null,
        ];

        var accepted = (bool)resolve.Invoke(null, args)!;

        Assert.That(accepted, Is.False);
        Assert.That(args[2]?.ToString(), Does.Contain("finite and non-zero"));
    }

    private static MethodInfo RequireStaticMethod(string name) =>
        ExecutorType.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new AssertionException($"Expected private static method '{name}' was not found.");
}
