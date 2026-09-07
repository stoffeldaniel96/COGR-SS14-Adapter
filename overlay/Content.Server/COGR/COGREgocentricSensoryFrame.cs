using System.Numerics;
using COGR.Core.Time;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Server.COGR;

/// <summary>
/// One immutable adapter-authoritative egocentric origin epoch used to transduce a bounded sensory frame.
/// This is physical sensor-frame state, not cognition-owned self-location or belief state.
/// </summary>
internal readonly record struct COGREgocentricSensoryFrame
{
    internal COGREgocentricSensoryFrame(
        EntityUid sensorEntity,
        EntityCoordinates origin,
        Angle localRotation,
        SimTick observedAtTick)
    {
        if (sensorEntity == EntityUid.Invalid)
            throw new ArgumentException("A valid physical sensor entity is required.", nameof(sensorEntity));
        if (origin.EntityId == EntityUid.Invalid)
            throw new ArgumentException("A valid sensor-frame parent is required.", nameof(origin));
        if (!float.IsFinite(origin.Position.X) || !float.IsFinite(origin.Position.Y))
            throw new ArgumentException("Sensor-frame origin must be finite.", nameof(origin));
        if (!double.IsFinite(localRotation.Theta))
            throw new ArgumentException("Sensor-frame rotation must be finite.", nameof(localRotation));

        SensorEntity = sensorEntity;
        Origin = origin;
        LocalRotation = localRotation;
        ObservedAtTick = observedAtTick;
    }

    /// <summary>The authoritative host entity providing this modality's egocentric origin.</summary>
    internal EntityUid SensorEntity { get; }

    /// <summary>The exactly sampled parent-local origin used by every spatial observation in this frame.</summary>
    internal EntityCoordinates Origin { get; }

    /// <summary>The exactly sampled parent-local orientation used by every spatial observation in this frame.</summary>
    internal Angle LocalRotation { get; }

    /// <summary>The simulation tick at which this immutable sensory origin epoch was sampled.</summary>
    internal SimTick ObservedAtTick { get; }

    /// <summary>
    /// Returns the native parent-frame offset from this sampled sensor origin to a target coordinate.
    /// A target outside the sampled origin's parent frame cannot be directly expressed by this frame.
    /// </summary>
    internal bool TryGetParentOffset(EntityCoordinates target, out Vector2 parentOffset)
    {
        parentOffset = Vector2.Zero;
        if (target.EntityId != Origin.EntityId ||
            !float.IsFinite(target.Position.X) ||
            !float.IsFinite(target.Position.Y))
        {
            return false;
        }

        parentOffset = target.Position - Origin.Position;
        return float.IsFinite(parentOffset.X) && float.IsFinite(parentOffset.Y);
    }

    /// <summary>
    /// Projects a target from this exact sampled origin into COGR's canonical owner-relative local basis.
    /// The sensor origin itself therefore projects to exactly zero by construction.
    /// </summary>
    internal bool TryProject(
        EntityCoordinates target,
        out double forwardLocal,
        out double leftLocal,
        out double distanceLocal)
    {
        forwardLocal = 0.0;
        leftLocal = 0.0;
        distanceLocal = 0.0;

        if (!TryGetParentOffset(target, out var parentOffset) ||
            !COGREmbodimentSpatialProjection.TryParentOffsetToOwnerRelativeLocal(
                parentOffset,
                LocalRotation,
                out forwardLocal,
                out leftLocal))
        {
            return false;
        }

        try
        {
            distanceLocal = COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                parentOffset.Length());
        }
        catch (ArgumentException)
        {
            forwardLocal = 0.0;
            leftLocal = 0.0;
            distanceLocal = 0.0;
            return false;
        }

        return double.IsFinite(distanceLocal);
    }
}
