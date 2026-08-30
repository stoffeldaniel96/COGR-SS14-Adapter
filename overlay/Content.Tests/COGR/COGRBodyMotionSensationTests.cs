using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server.COGR.Systems;
using Content.Shared.Mobs;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.COGR;

[TestFixture]
[TestOf(typeof(COGRBodyMotionSensationSystem))]
public sealed class COGRBodyMotionSensationTests
{
    private static readonly Type SystemType = typeof(COGRBodyMotionSensationSystem);

    [Test]
    public void DepartureBodyFrame_UsesBodyRelativeRatherThanWorldDirection()
    {
        var project = RequireStaticMethod(
            "ProjectIntoDepartureBodyFrame",
            typeof(Vector2),
            typeof(Angle));
        var quantize = RequireStaticMethod("QuantizeBearing", typeof(Vector2));

        var worldPositiveX = new Vector2(1f, 0f);
        var unrotated = (Vector2)project.Invoke(null, [worldPositiveX, Angle.Zero])!;
        var quarterTurn = (Vector2)project.Invoke(
            null,
            [worldPositiveX, new Angle(Math.PI / 2d)])!;

        Assert.That(quantize.Invoke(null, [unrotated])!.ToString(), Is.EqualTo("Forward"));
        Assert.That(quantize.Invoke(null, [quarterTurn])!.ToString(), Is.EqualTo("Right"));
    }

    [Test]
    public void DurationBands_AreBoundedPsychophysicalCategories()
    {
        var classify = RequireStaticMethod("ClassifyDuration", typeof(TimeSpan));

        AssertDuration(classify, TimeSpan.Zero, "Momentary");
        AssertDuration(classify, TimeSpan.FromMilliseconds(250), "Momentary");
        AssertDuration(classify, TimeSpan.FromMilliseconds(251), "Brief");
        AssertDuration(classify, TimeSpan.FromMilliseconds(900), "Brief");
        AssertDuration(classify, TimeSpan.FromMilliseconds(901), "Sustained");
        AssertDuration(classify, TimeSpan.FromMilliseconds(2500), "Sustained");
        AssertDuration(classify, TimeSpan.FromMilliseconds(2501), "Extended");
        AssertDuration(classify, TimeSpan.FromMinutes(10), "Extended");
    }

    [Test]
    public void RotationProjection_IsCoarseAndWrapsToBoundedOctants()
    {
        var quantize = RequireStaticMethod("QuantizeRotationOctants", typeof(double));

        Assert.That((int)quantize.Invoke(null, [Math.PI / 4d])!, Is.EqualTo(1));
        Assert.That((int)quantize.Invoke(null, [-Math.PI / 4d])!, Is.EqualTo(-1));
        Assert.That((int)quantize.Invoke(null, [Math.PI])!, Is.EqualTo(4));
        Assert.That((int)quantize.Invoke(null, [3d * Math.PI / 2d])!, Is.EqualTo(-2));
    }

    [Test]
    public void CriticalBody_RemainsEligibleForPassiveMotionSensation()
    {
        var viable = RequireStaticMethod("IsMotionSenseViable", typeof(MobState));

        Assert.That((bool)viable.Invoke(null, [MobState.Alive])!, Is.True);
        Assert.That((bool)viable.Invoke(null, [MobState.Critical])!, Is.True);
        Assert.That((bool)viable.Invoke(null, [MobState.Dead])!, Is.False);
        Assert.That((bool)viable.Invoke(null, [MobState.Invalid])!, Is.False);
    }

    [Test]
    public void DirectionChanges_AreComparedAsCoarseSectors()
    {
        var distance = RequireStaticMethod("BearingSectorDistance", 2);
        var bearingType = distance.GetParameters()[0].ParameterType;

        object Bearing(string name) => Enum.Parse(bearingType, name, ignoreCase: false);

        Assert.That(
            (int)distance.Invoke(null, [Bearing("Forward"), Bearing("ForwardLeft")])!,
            Is.EqualTo(1));
        Assert.That(
            (int)distance.Invoke(null, [Bearing("Forward"), Bearing("Left")])!,
            Is.EqualTo(2));
        Assert.That(
            (int)distance.Invoke(null, [Bearing("ForwardRight"), Bearing("ForwardLeft")])!,
            Is.EqualTo(2));
    }

    [Test]
    public void AggregationCadence_RefreshesContinuousMotionWithinBriefBand()
    {
        var quiet = RequireStaticTimeSpan("MotionQuietPeriod");
        var maximum = RequireStaticTimeSpan("MaximumMotionInterval");
        var briefMaximum = RequireStaticTimeSpan("BriefMaximum");

        Assert.That(quiet, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100)));
        Assert.That(maximum, Is.GreaterThan(quiet));
        Assert.That(maximum, Is.LessThanOrEqualTo(briefMaximum));
    }

    [Test]
    public void PendingAggregation_DoesNotRetainEventCountDistanceSpeedOrTicks()
    {
        var pendingType = SystemType.GetNestedType("PendingMotion", BindingFlags.NonPublic);
        Assert.That(pendingType, Is.Not.Null);

        var names = pendingType!
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(static property => property.Name)
            .Concat(
                pendingType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(static field => field.Name))
            .ToArray();
        var hasOdometerLikeState = names.Any(static name =>
            name.Contains("Count", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Distance", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Speed", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Tick", StringComparison.OrdinalIgnoreCase));

        Assert.That(hasOdometerLikeState, Is.False);
    }

    [Test]
    public void AuthorityAggregationKey_IncludesBodyGeneration()
    {
        var keyType = SystemType.GetNestedType("MotionAuthorityKey", BindingFlags.NonPublic);
        Assert.That(keyType, Is.Not.Null);

        var generation = keyType!.GetProperty(
            "BodyGeneration",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(generation, Is.Not.Null);
        Assert.That(generation!.PropertyType, Is.EqualTo(typeof(uint)));
    }

    [Test]
    public void MotionSensation_HasNoMotorActionBlockerDependency()
    {
        var dependencyTypes = SystemType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(static field => field.FieldType.FullName ?? field.FieldType.Name)
            .ToArray();
        var hasMotorBlockerDependency = dependencyTypes.Any(static typeName =>
            string.Equals(
                typeName,
                "Content.Shared.ActionBlocker.ActionBlockerSystem",
                StringComparison.Ordinal));

        Assert.That(hasMotorBlockerDependency, Is.False);
    }

    private static MethodInfo RequireStaticMethod(string name, params Type[] parameterTypes)
    {
        var method = SystemType.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        Assert.That(method, Is.Not.Null, $"Expected production helper '{name}' was not found.");
        return method!;
    }

    private static MethodInfo RequireStaticMethod(string name, int parameterCount)
    {
        var method = SystemType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal) &&
                candidate.GetParameters().Length == parameterCount);
        Assert.That(method, Is.Not.Null, $"Expected production helper '{name}' was not found.");
        return method!;
    }

    private static TimeSpan RequireStaticTimeSpan(string fieldName)
    {
        var field = SystemType.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Expected production cadence field '{fieldName}' was not found.");
        return (TimeSpan)field!.GetValue(null)!;
    }

    private static void AssertDuration(
        MethodInfo classify,
        TimeSpan duration,
        string expected)
    {
        Assert.That(classify.Invoke(null, [duration])!.ToString(), Is.EqualTo(expected));
    }
}
