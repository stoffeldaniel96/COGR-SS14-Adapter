using System;

namespace Content.Server.COGR;

/// <summary>
/// Adapter-owned calibration between SS14 native spatial units and the normalized embodiment-local scale used at the
/// cognition boundary. COGR never receives this conversion factor and never needs to know SS14's map/tile convention.
/// The same calibration must be used in both perception transduction and realization of cognition-authored local vectors
/// so adapter-side scale errors do not compound across the round trip.
/// </summary>
internal static class COGREmbodimentSpatialCalibration
{
    internal const string GenericHumanoidProfile = "ss14.generic-humanoid.v1";

    // BaseSpeciesMob currently models the generic humanoid collision body as a circle with radius 0.35 native units.
    // V1 deliberately normalizes one adapter-local spatial unit to that modeled planar body extent (diameter = 0.70).
    // This gives the Coggent's seeded body-length prior a roughly aligned starting scale without making adapter calibration
    // itself a cognitive belief. Future embodiment profiles should supply their own calibrated local scale.
    private const double GenericHumanoidNativeUnitsPerLocalScaleUnit = 0.70d;

    internal static double NativeUnitsToLocalUnits(string embodimentProfile, double nativeUnits)
    {
        if (!double.IsFinite(nativeUnits))
            throw new ArgumentOutOfRangeException(nameof(nativeUnits));

        return nativeUnits / NativeUnitsPerLocalScaleUnit(embodimentProfile);
    }

    internal static double LocalUnitsToNativeUnits(string embodimentProfile, double localUnits)
    {
        if (!double.IsFinite(localUnits))
            throw new ArgumentOutOfRangeException(nameof(localUnits));

        return localUnits * NativeUnitsPerLocalScaleUnit(embodimentProfile);
    }

    private static double NativeUnitsPerLocalScaleUnit(string embodimentProfile) =>
        string.Equals(embodimentProfile, GenericHumanoidProfile, StringComparison.Ordinal)
            ? GenericHumanoidNativeUnitsPerLocalScaleUnit
            : throw new ArgumentException(
                $"No SS14 local spatial calibration is registered for embodiment profile '{embodimentProfile}'.",
                nameof(embodimentProfile));
}
