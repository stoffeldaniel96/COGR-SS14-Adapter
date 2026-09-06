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
    private static readonly Type ProjectionType = typeof(COGRActionExecutor).Assembly.GetType(
        "Content.Server.COGR.COGREmbodimentSpatialProjection",
        throwOnError: true)!;

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
    public void CurrentBodyRotation_ProjectsObjectiveThroughSharedParentFrameTransform()
    {
        var project = RequireProjectionMethod("TryOwnerRelativeLocalToParentOffset");
        object?[] args =
        [
            1d,
            0d,
            new Angle(Math.PI / 2d),
            Vector2.Zero,
            Vector2.Zero,
        ];

        var accepted = (bool)project.Invoke(null, args)!;

        Assert.That(accepted, Is.True);
        var ownerRelativeNative = (Vector2)args[3]!;
        var parentOffset = (Vector2)args[4]!;
        Assert.That(ownerRelativeNative.X, Is.EqualTo(0.70f).Within(0.00001f));
        Assert.That(ownerRelativeNative.Y, Is.EqualTo(0f).Within(0.00001f));
        Assert.That(parentOffset.X, Is.EqualTo(0f).Within(0.00001f));
        Assert.That(parentOffset.Y, Is.EqualTo(0.70f).Within(0.00001f));
    }

    [TestCase(0d)]
    [TestCase(0.37d)]
    [TestCase(1.5707963267948966d)]
    [TestCase(-2.2d)]
    public void SharedParentFrameProjection_InverseRecoversOwnerRelativeLocalVector(double rotationRadians)
    {
        var project = RequireProjectionMethod("TryOwnerRelativeLocalToParentOffset");
        object?[] forwardArgs =
        [
            1.25d,
            -0.4d,
            new Angle(rotationRadians),
            Vector2.Zero,
            Vector2.Zero,
        ];

        var projected = (bool)project.Invoke(null, forwardArgs)!;
        Assert.That(projected, Is.True);
        var parentOffset = (Vector2)forwardArgs[4]!;

        var inverse = RequireProjectionMethod("TryParentOffsetToOwnerRelativeLocal");
        object?[] inverseArgs =
        [
            parentOffset,
            new Angle(rotationRadians),
            0d,
            0d,
        ];

        var recovered = (bool)inverse.Invoke(null, inverseArgs)!;

        Assert.That(recovered, Is.True);
        Assert.That((double)inverseArgs[2]!, Is.EqualTo(1.25d).Within(0.00001d));
        Assert.That((double)inverseArgs[3]!, Is.EqualTo(-0.4d).Within(0.00001d));
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

    [TestCase(0.10f, 0.20f, true)]
    [TestCase(0.20f, 0.20f, true)]
    [TestCase(0.21f, 0.20f, false)]
    public void ProjectedObjectiveWithinNativeArrivalRange_IsImmediateSuccessfulNoOp(
        float directDistance,
        float arrivalTolerance,
        bool expected)
    {
        var check = RequireStaticMethod("IsProjectedObjectiveAlreadyWithinArrivalTolerance");

        var result = (bool)check.Invoke(null, [directDistance, arrivalTolerance])!;

        Assert.That(result, Is.EqualTo(expected));
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

    private static MethodInfo RequireProjectionMethod(string name) =>
        ProjectionType.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new AssertionException($"Expected shared spatial projection method '{name}' was not found.");
}
