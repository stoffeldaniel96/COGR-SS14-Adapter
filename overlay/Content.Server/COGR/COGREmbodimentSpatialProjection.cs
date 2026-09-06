using System.Numerics;

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

        var nativeForward = COGREmbodimentSpatialCalibration.LocalUnitsToNativeUnits(
            COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
            forwardLocal);
        var nativeLeft = COGREmbodimentSpatialCalibration.LocalUnitsToNativeUnits(
            COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
            leftLocal);
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

    internal static Vector2 OwnerRelativeNativeToParentOffset(
        Vector2 ownerRelativeNative,
        Angle localRotation)
    {
        var cos = (float)Math.Cos(localRotation.Theta);
        var sin = (float)Math.Sin(localRotation.Theta);
        return new Vector2(
            ownerRelativeNative.X * cos - ownerRelativeNative.Y * sin,
            ownerRelativeNative.X * sin + ownerRelativeNative.Y * cos);
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

        forwardLocal = COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
            COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
            nativeForward);
        leftLocal = COGREmbodimentSpatialCalibration.NativeUnitsToLocalUnits(
            COGREmbodimentSpatialCalibration.GenericHumanoidProfile,
            nativeLeft);
        return double.IsFinite(forwardLocal) && double.IsFinite(leftLocal);
    }
}
