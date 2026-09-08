using System;
using System.Reflection;
using Content.Server.COGR.Systems;
using NUnit.Framework;

namespace Content.Tests.COGR;

[TestFixture]
[TestOf(typeof(COGRRegionalPerceptionRouterSystem))]
public sealed class COGRBodyMotionRoutingTests
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void TransformMoveHandler_OwnsControlledBodyMotionFanout()
    {
        var routerType = typeof(COGRRegionalPerceptionRouterSystem);
        var transformHandler = routerType.GetMethod("OnEntityMoved", InstanceNonPublic);
        var controlledFanout = routerType.GetMethod("RouteControlledBodyMovement", InstanceNonPublic);
        var legacyControlledHandler = routerType.GetMethod("OnControlledBodyMoved", InstanceNonPublic);
        var sensationIngress = typeof(COGRBodyMotionSensationSystem).GetMethod(
            "NotifyControlledBodyMoved",
            BindingFlags.Instance | BindingFlags.Public);

        Assert.That(transformHandler, Is.Not.Null);
        Assert.That(controlledFanout, Is.Not.Null);
        Assert.That(sensationIngress, Is.Not.Null);
        Assert.That(
            legacyControlledHandler,
            Is.Null,
            "Controlled-body motion must not depend on a second component-specific MoveEvent route.");

        var parameters = transformHandler!.GetParameters();
        Assert.That(parameters.Length, Is.EqualTo(3));
        Assert.That(parameters[1].ParameterType.Name, Is.EqualTo("TransformComponent"));
        Assert.That(parameters[2].ParameterType.IsByRef, Is.True);
        Assert.That(parameters[2].ParameterType.GetElementType()!.Name, Is.EqualTo("MoveEvent"));

        Assert.That(
            ContainsMethodReference(transformHandler, controlledFanout!),
            Is.True,
            "The authoritative TransformComponent MoveEvent handler must fan controlled movement into proprioception.");
        Assert.That(
            ContainsMethodReference(controlledFanout!, sensationIngress!),
            Is.True,
            "The controlled movement fanout must reach the passive body-motion sensation system.");
    }

    private static bool ContainsMethodReference(MethodInfo caller, MethodInfo callee)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray();
        if (il is null || il.Length < sizeof(int))
            return false;

        var token = BitConverter.GetBytes(callee.MetadataToken);
        for (var index = 0; index <= il.Length - token.Length; index++)
        {
            var matches = true;
            for (var offset = 0; offset < token.Length; offset++)
            {
                if (il[index + offset] == token[offset])
                    continue;

                matches = false;
                break;
            }

            if (matches)
                return true;
        }

        return false;
    }
}
