using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using COGR.Contracts.Messages;
using COGR.Core.Actions;
using COGR.Core.Actions.Parameters;
using COGR.Core.Identifiers;
using COGR.Core.Perception;
using COGR.Core.Sequences;
using COGR.Core.Time;
using Content.Server.Construction.Components;
using Content.Server.DeviceLinking.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Storage.Components;
using Content.Shared.Tag;
using Content.Shared.Tools.Components;
using Robust.Shared.Containers;
using Robust.Shared.Log;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

public sealed partial class COGRBoundedPerceptionSystem
{
    private const int LocalSpatialComponentDecimalPlaces = 6;

    private void AddSpatialFeatures(
        List<ObservedFeature> features,
        EntityUid observer,
        EntityUid target,
        double distance)
    {
        var observerTransform = Transform(observer);
        var targetTransform = Transform(target);
        var observerCoordinates = observerTransform.Coordinates;
        var targetCoordinates = targetTransform.Coordinates;
        if (observerCoordinates.EntityId != targetCoordinates.EntityId)
            return;

        var delta = targetCoordinates.Position - observerCoordinates.Position;

        // Coordinates and LocalRotation share the same parent frame here. Rotate the parent-frame offset back through the
        // observer's local rotation so the transport describes the target relative to the observer's embodied frame rather
        // than a map/cardinal frame. SS14 rotation zero faces local +X, therefore local +X is forward and +Y is left.
        // Current SS14 geometry is planar, so +Z (up) is explicitly zero rather than omitted from the spatial contract.
        var theta = observerTransform.LocalRotation.Theta;
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        var actorRelativeNativeX = (delta.X * cos) + (delta.Y * sin);
        var actorRelativeNativeY = (-delta.X * sin) + (delta.Y * cos);

        var localX = QuantizeLocalComponent(
            COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                actorRelativeNativeX));
        var localY = QuantizeLocalComponent(
            COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                actorRelativeNativeY));
        const double localZ = 0.0d;
        var localDistance = QuantizeLocalComponent(
            COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                distance));

        features.Add(ObservedFeature.LocalX(localX, 0.95));
        features.Add(ObservedFeature.LocalY(localY, 0.95));
        features.Add(ObservedFeature.LocalZ(localZ, 0.95));
        features.Add(ObservedFeature.LocalDistance(localDistance, 0.95));
    }

    private void AddMotionFeature(
        List<ObservedFeature> features,
        EntityUid entity,
        string category)
    {
        if (Transform(entity).Anchored && category != "actor")
            return;

        var moving = TryComp<PhysicsComponent>(entity, out var physics) &&
            physics.LinearVelocity.LengthSquared() > MovingVelocitySquaredThreshold;
        features.Add(ObservedFeature.State(moving ? "moving" : "stationary", 0.8));
    }

    private static double QuantizeLocalComponent(double localUnits) =>
        Math.Round(
            localUnits,
            LocalSpatialComponentDecimalPlaces,
            MidpointRounding.AwayFromZero);
}
