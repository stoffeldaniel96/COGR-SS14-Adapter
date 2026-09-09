using System;
using System.Reflection;
using System.Reflection.Emit;
using Content.Server.COGR;
using Content.Server.COGR.Systems;
using NUnit.Framework;

namespace Content.Tests.COGR;

[TestFixture]
[TestOf(typeof(COGRSpatialVisualizationSystem))]
public sealed class COGRSpatialDiagnosticCausalFrameTests
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;

    [Test]
    public void AdministrativeSnapshot_FlushesQueuedEnvironmentEvidenceBeforeClaimingSourceSequence()
    {
        var managerType = typeof(COGRConnectionManager);
        var sendAdministrative = managerType.GetMethod(
            nameof(COGRConnectionManager.SendAdministrativeCommand),
            InstancePublic);
        var flushPending = managerType.GetMethod("FlushPendingEnvironmentMessages", InstanceNonPublic);
        var nextSourceSequence = managerType.GetMethod("NextSourceSequence", InstanceNonPublic);

        Assert.That(sendAdministrative, Is.Not.Null);
        Assert.That(flushPending, Is.Not.Null);
        Assert.That(nextSourceSequence, Is.Not.Null);

        var flushOffset = RequireCallOffset(sendAdministrative!, flushPending!);
        var sequenceOffset = RequireCallOffset(sendAdministrative, nextSourceSequence!);

        Assert.That(
            flushOffset,
            Is.LessThan(sequenceOffset),
            "A diagnostic snapshot must drain already-produced environment evidence before taking its source sequence.");
    }

    [Test]
    public void SpatialPoll_CapturesBodyFrameAfterClosingPendingProprioception()
    {
        var systemType = typeof(COGRSpatialVisualizationSystem);
        var capture = systemType.GetMethod("TryCaptureCausalPollBodyFrame", InstanceNonPublic);
        var sendPoll = systemType.GetMethod("SendPoll", InstanceNonPublic);
        var motionBoundary = typeof(COGRBodyMotionSensationSystem).GetMethod(
            nameof(COGRBodyMotionSensationSystem.NotifyVisualSamplingBoundary),
            InstancePublic);
        var sendAdministrative = typeof(COGRConnectionManager).GetMethod(
            nameof(COGRConnectionManager.SendAdministrativeCommand),
            InstancePublic);

        Assert.That(capture, Is.Not.Null);
        Assert.That(sendPoll, Is.Not.Null);
        Assert.That(motionBoundary, Is.Not.Null);
        Assert.That(sendAdministrative, Is.Not.Null);
        Assert.That(
            ContainsMethodReference(capture!, motionBoundary!),
            Is.True,
            "The diagnostic poll frame must close pending owner motion before sampling the host body frame.");

        var captureOffset = RequireCallOffset(sendPoll!, capture);
        var sendOffset = RequireCallOffset(sendPoll, sendAdministrative!);
        Assert.That(
            captureOffset,
            Is.LessThan(sendOffset),
            "The exact embodied diagnostic frame must be captured before the Runtime snapshot command is emitted.");
    }

    [Test]
    public void SpatialResponse_RequiresCapturedPollFrameInsteadOfResolvingResponseTimeBodyPose()
    {
        var systemType = typeof(COGRSpatialVisualizationSystem);
        var resolvePayload = systemType.GetMethod("ResolvePayload", InstanceNonPublic);
        var responseHandler = systemType.GetMethod("OnAdministrativeResponse", InstanceNonPublic);
        var legacyResponseTimeResolver = systemType.GetMethod("TryResolveBodyFrame", InstanceNonPublic);

        Assert.That(resolvePayload, Is.Not.Null);
        Assert.That(responseHandler, Is.Not.Null);
        Assert.That(
            legacyResponseTimeResolver,
            Is.Null,
            "Spatial visualization must not re-resolve a newer body pose when an older Runtime-local snapshot returns.");

        var parameters = resolvePayload!.GetParameters();
        Assert.That(parameters.Length, Is.EqualTo(3));
        Assert.That(
            parameters[2].ParameterType.Name,
            Is.EqualTo("SpatialPollBodyFrame"),
            "Response realization must explicitly consume the causal poll body frame.");
        Assert.That(
            ContainsMethodReference(responseHandler!, resolvePayload),
            Is.True,
            "Administrative response handling must realize the Runtime snapshot through the captured poll frame.");
    }

    private static int RequireCallOffset(MethodInfo caller, MethodInfo callee)
    {
        foreach (var call in EnumerateCalls(caller))
        {
            if (MethodsMatch(call.Method, callee))
                return call.Offset;
        }

        throw new AssertionException($"Expected {caller.Name} to call {callee.DeclaringType?.Name}.{callee.Name}.");
    }

    private static bool ContainsMethodReference(MethodInfo caller, MethodInfo callee)
    {
        foreach (var call in EnumerateCalls(caller))
        {
            if (MethodsMatch(call.Method, callee))
                return true;
        }

        return false;
    }

    private static bool MethodsMatch(MethodBase actual, MethodInfo expected)
    {
        if (actual is not MethodInfo method
            || method.DeclaringType != expected.DeclaringType
            || method.Name != expected.Name)
        {
            return false;
        }

        var actualParameters = method.GetParameters();
        var expectedParameters = expected.GetParameters();
        if (actualParameters.Length != expectedParameters.Length)
            return false;

        for (var index = 0; index < actualParameters.Length; index++)
        {
            if (actualParameters[index].ParameterType != expectedParameters[index].ParameterType)
                return false;
        }

        return true;
    }

    private static IEnumerable<(int Offset, MethodBase Method)> EnumerateCalls(MethodInfo caller)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray();
        if (il is null || il.Length < 5)
            yield break;

        var call = unchecked((byte)OpCodes.Call.Value);
        var callVirt = unchecked((byte)OpCodes.Callvirt.Value);
        for (var index = 0; index <= il.Length - 5; index++)
        {
            if (il[index] != call && il[index] != callVirt)
                continue;

            var token = BitConverter.ToInt32(il, index + 1);
            MethodBase? resolved;
            try
            {
                resolved = caller.Module.ResolveMethod(token);
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            if (resolved is not null)
                yield return (index, resolved);
        }
    }
}
