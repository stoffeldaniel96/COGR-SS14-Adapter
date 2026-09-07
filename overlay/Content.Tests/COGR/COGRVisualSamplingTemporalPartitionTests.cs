using System;
using System.Linq;
using System.Reflection;
using Content.Server.COGR.Systems;
using NUnit.Framework;

namespace Content.Tests.COGR;

[TestFixture]
[TestOf(typeof(COGRBoundedPerceptionSystem))]
public sealed class COGRVisualSamplingTemporalPartitionTests
{
    [Test]
    public void AuthoritativeVisualProjector_OwnsBodyMotionPartitionDependency()
    {
        var bodyMotion = typeof(COGRBoundedPerceptionSystem).GetField(
            "_bodyMotion",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(bodyMotion, Is.Not.Null);
        Assert.That(bodyMotion!.FieldType, Is.EqualTo(typeof(COGRBodyMotionSensationSystem)));

        var boundary = typeof(COGRBodyMotionSensationSystem).GetMethod(
            "NotifyVisualSamplingBoundary",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.That(boundary, Is.Not.Null);
        Assert.That(boundary!.ReturnType, Is.EqualTo(typeof(void)));

        // ProjectReplica is the one authoritative visual projector shared by semantic-replica
        // sampling and focused/active visual requests. Keeping the body-motion dependency here
        // prevents one acquisition path from silently bypassing the V3 temporal partition.
        var projector = typeof(COGRBoundedPerceptionSystem).GetMethod(
            "ProjectReplica",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.That(projector, Is.Not.Null);
    }

    [Test]
    public void AuthoritativeVisualProjector_HasOneImmutableEgocentricOriginEpoch()
    {
        var serverAssembly = typeof(COGRBoundedPerceptionSystem).Assembly;
        var frameType = serverAssembly.GetType(
            "Content.Server.COGR.COGREgocentricSensoryFrame",
            throwOnError: true)!;

        Assert.That(frameType.IsValueType, Is.True);
        Assert.That(
            frameType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(static property => property.Name),
            Does.Contain("SensorEntity"));
        Assert.That(
            frameType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(static property => property.Name),
            Does.Contain("Origin"));
        Assert.That(
            frameType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(static property => property.Name),
            Does.Contain("LocalRotation"));
        Assert.That(
            frameType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(static property => property.Name),
            Does.Contain("ObservedAtTick"));

        var capture = typeof(COGRBoundedPerceptionSystem).GetMethod(
            "TryCaptureEgocentricSensoryFrame",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(capture, Is.Not.Null);
        Assert.That(capture!.ReturnType, Is.EqualTo(typeof(bool)));
        Assert.That(capture.GetParameters().Last().IsOut, Is.True);
        Assert.That(capture.GetParameters().Last().ParameterType.GetElementType(), Is.EqualTo(frameType));

        var project = frameType.GetMethod(
            "TryProject",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(project, Is.Not.Null);
        Assert.That(project!.ReturnType, Is.EqualTo(typeof(bool)));
    }
}
