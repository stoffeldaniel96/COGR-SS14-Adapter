using System;
using System.Reflection;
using Content.Server.COGR.Systems;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.Tests.COGR;

[TestFixture]
[TestOf(typeof(COGRRegionalPerceptionRouterSystem))]
public sealed class COGRBodyMotionRoutingTests
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;

    [Test]
    public void GlobalTransformMoveHook_OwnsControlledBodyMotionFanout()
    {
        var routerType = typeof(COGRRegionalPerceptionRouterSystem);
        var initialize = routerType.GetMethod(nameof(COGRRegionalPerceptionRouterSystem.Initialize), InstancePublic);
        var shutdown = routerType.GetMethod(nameof(COGRRegionalPerceptionRouterSystem.Shutdown), InstancePublic);
        var transformHandler = routerType.GetMethod("OnEntityMoved", InstanceNonPublic);
        var controlledFanout = routerType.GetMethod("RouteControlledBodyMovement", InstanceNonPublic);
        var legacyControlledHandler = routerType.GetMethod("OnControlledBodyMoved", InstanceNonPublic);
        var sensationIngress = typeof(COGRBodyMotionSensationSystem).GetMethod(
            "NotifyControlledBodyMoved",
            InstancePublic);
        var globalMoveEvent = typeof(SharedTransformSystem).GetEvent(
            nameof(SharedTransformSystem.OnGlobalMoveEvent),
            InstancePublic);
        var addGlobalMoveHandler = globalMoveEvent?.GetAddMethod();
        var removeGlobalMoveHandler = globalMoveEvent?.GetRemoveMethod();

        Assert.That(initialize, Is.Not.Null);
        Assert.That(shutdown, Is.Not.Null);
        Assert.That(transformHandler, Is.Not.Null);
        Assert.That(controlledFanout, Is.Not.Null);
        Assert.That(sensationIngress, Is.Not.Null);
        Assert.That(globalMoveEvent, Is.Not.Null);
        Assert.That(addGlobalMoveHandler, Is.Not.Null);
        Assert.That(removeGlobalMoveHandler, Is.Not.Null);
        Assert.That(
            legacyControlledHandler,
            Is.Null,
            "Controlled-body motion must not depend on a second component-specific MoveEvent route.");

        var parameters = transformHandler!.GetParameters();
        Assert.That(parameters.Length, Is.EqualTo(1));
        Assert.That(parameters[0].ParameterType.IsByRef, Is.True);
        Assert.That(parameters[0].ParameterType.GetElementType()!.Name, Is.EqualTo("MoveEvent"));

        Assert.That(
            ContainsMethodReference(initialize!, addGlobalMoveHandler!),
            Is.True,
            "The regional router must subscribe to RobustToolbox's global transform movement hook during initialization.");
        Assert.That(
            ContainsMethodReference(shutdown!, removeGlobalMoveHandler!),
            Is.True,
            "The regional router must release the global transform movement hook during shutdown.");
        Assert.That(
            ContainsMethodReference(transformHandler, controlledFanout!),
            Is.True,
            "The global transform movement handler must fan controlled movement into proprioception.");
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
