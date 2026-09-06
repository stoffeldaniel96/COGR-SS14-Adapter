using System;
using System.Reflection;
using Content.Server.COGR.Systems;
using Content.Server.NPC.Components;
using NUnit.Framework;

namespace Content.Tests.COGR;

[TestFixture]
[TestOf(typeof(COGRLocomotorRealizabilitySystem))]
public sealed class COGRLocomotorRealizabilityTests
{
    private static readonly Type SystemType = typeof(COGRLocomotorRealizabilitySystem);

    [Test]
    public void NativeSteeringRange_IsNormalizedThroughEmbodimentCalibration()
    {
        var resolve = RequireStaticMethod("TryResolveMinimumReliableProjectedDisplacementLocalUnits");
        var nativeRange = new NPCSteeringComponent().Range;
        object?[] args =
        [
            nativeRange,
            0.0d,
        ];

        var accepted = (bool)resolve.Invoke(null, args)!;

        Assert.That(accepted, Is.True);
        Assert.That(nativeRange, Is.EqualTo(0.20f));
        Assert.That((double)args[1]!, Is.EqualTo(0.20d / 0.70d).Within(0.0000001d));
    }

    [TestCase(0f)]
    [TestCase(-0.1f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void InvalidNativeSteeringRange_DoesNotBecomeCognitiveEvidence(float nativeRange)
    {
        var resolve = RequireStaticMethod("TryResolveMinimumReliableProjectedDisplacementLocalUnits");
        object?[] args =
        [
            nativeRange,
            0.0d,
        ];

        var accepted = (bool)resolve.Invoke(null, args)!;

        Assert.That(accepted, Is.False);
        Assert.That((double)args[1]!, Is.EqualTo(0.0d));
    }

    private static MethodInfo RequireStaticMethod(string name) =>
        SystemType.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new AssertionException($"Expected private static method '{name}' was not found.");
}
