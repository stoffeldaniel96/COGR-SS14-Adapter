using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server.COGR;

/// <summary>
/// Adapter-owned paired projection between COGR's owner-relative planar local basis and the current Station parent frame.
/// Perception, movement realization, and privileged diagnostics must share this exact transform so sign/axis conventions
/// cannot silently diverge between sensing, acting, and observation.
/// </summary>
internal static class COGREmbodimentSpatialProjection
{
    internal static bool TryOwnerRelativeLocalToNative(
        double forwardLocal,
        double leftLocal,
        out Vector2 ownerRelativeNative)
    {
        ownerRelativeNative = Vector2.Zero;
        if (!double.IsFinite(forwardLocal) || !double.IsFinite(leftLocal))
            return false;

        double nativeForward;
        double nativeLeft;
        try
        {
            nativeForward = COGREmbodimentSpatialCalibration.LocalUnitsToNativeUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                forwardLocal);
            nativeLeft = COGREmbodimentSpatialCalibration.LocalUnitsToNativeUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                leftLocal);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!double.IsFinite(nativeForward)
            || !double.IsFinite(nativeLeft)
            || nativeForward > float.MaxValue
            || nativeForward < float.MinValue
            || nativeLeft > float.MaxValue
            || nativeLeft < float.MinValue)
        {
            return false;
        }

        ownerRelativeNative = new Vector2((float)nativeForward, (float)nativeLeft);
        return float.IsFinite(ownerRelativeNative.X) && float.IsFinite(ownerRelativeNative.Y);
    }

    internal static bool TryOwnerRelativeLocalToParentOffset(
        double forwardLocal,
        double leftLocal,
        Angle localRotation,
        out Vector2 ownerRelativeNative,
        out Vector2 parentOffset)
    {
        ownerRelativeNative = Vector2.Zero;
        parentOffset = Vector2.Zero;
        if (!TryOwnerRelativeLocalToNative(forwardLocal, leftLocal, out ownerRelativeNative))
            return false;

        var cos = (float)Math.Cos(localRotation.Theta);
        var sin = (float)Math.Sin(localRotation.Theta);
        parentOffset = new Vector2(
            ownerRelativeNative.X * cos - ownerRelativeNative.Y * sin,
            ownerRelativeNative.X * sin + ownerRelativeNative.Y * cos);
        return float.IsFinite(parentOffset.X) && float.IsFinite(parentOffset.Y);
    }

    /// <summary>
    /// Forms the exact parent-local Station endpoint used to realize a cognition-authored owner-relative local point.
    /// Consumers that need map coordinates must convert this returned <see cref="EntityCoordinates"/> through the shared
    /// transform system rather than re-projecting the vector through independent world-space rotation math.
    /// </summary>
    internal static bool TryOwnerRelativeLocalToParentCoordinates(
        EntityUid parentUid,
        Vector2 ownerParentPosition,
        Angle localRotation,
        double forwardLocal,
        double leftLocal,
        out EntityCoordinates endpoint,
        out Vector2 ownerRelativeNative,
        out Vector2 parentOffset)
    {
        endpoint = default;
        ownerRelativeNative = Vector2.Zero;
        parentOffset = Vector2.Zero;

        if (parentUid == EntityUid.Invalid
            || !float.IsFinite(ownerParentPosition.X)
            || !float.IsFinite(ownerParentPosition.Y)
            || !TryOwnerRelativeLocalToParentOffset(
                forwardLocal,
                leftLocal,
                localRotation,
                out ownerRelativeNative,
                out parentOffset))
        {
            return false;
        }

        var targetPosition = ownerParentPosition + parentOffset;
        if (!float.IsFinite(targetPosition.X) || !float.IsFinite(targetPosition.Y))
            return false;

        endpoint = new EntityCoordinates(parentUid, targetPosition);
        return true;
    }

    internal static bool TryParentOffsetToOwnerRelativeLocal(
        Vector2 parentNativeOffset,
        Angle localRotation,
        out double forwardLocal,
        out double leftLocal)
    {
        forwardLocal = 0.0;
        leftLocal = 0.0;
        if (!float.IsFinite(parentNativeOffset.X) || !float.IsFinite(parentNativeOffset.Y))
            return false;

        var cos = Math.Cos(localRotation.Theta);
        var sin = Math.Sin(localRotation.Theta);
        var nativeForward = (parentNativeOffset.X * cos) + (parentNativeOffset.Y * sin);
        var nativeLeft = (-parentNativeOffset.X * sin) + (parentNativeOffset.Y * cos);

        try
        {
            forwardLocal = COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                nativeForward);
            leftLocal = COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
                COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
                nativeLeft);
        }
        catch (ArgumentException)
        {
            forwardLocal = 0.0;
            leftLocal = 0.0;
            return false;
        }

        return double.IsFinite(forwardLocal) && double.IsFinite(leftLocal);
    }
}
