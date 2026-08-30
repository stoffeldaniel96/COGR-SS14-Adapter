namespace Content.Server.COGR;

/// <summary>
/// Adapter-owned spatial execution limits shared by actor-relative perception and bounded locomotion.
/// These are authoritative environment execution limits, not Coggent beliefs about its body, vision, or possible reach.
/// </summary>
public static class COGRSpatialPolicy
{
    /// <summary>
    /// Current V1 normal embodied visual horizon. Perception may expose actor-relative evidence this far away.
    /// </summary>
    public const float DefaultVisualHorizon = 12.0f;

    /// <summary>
    /// Fraction of a candidate world-space AABB half-extent used for visual sample points. Keeping samples slightly
    /// inside the footprint avoids numerical edge leakage while still allowing an exposed edge to be perceived.
    /// </summary>
    public const float VisualFootprintSampleExtentFraction = 0.9f;

    /// <summary>Number of bounded visual samples used for one candidate footprint (center, four edges, four corners).</summary>
    public const int VisualFootprintSampleCount = 9;

    /// <summary>
    /// Minimum fraction of candidate footprint samples that must have a clear observer ray for the entity to be
    /// perceptible. One exposed sample is sufficient; confidence carries how partial that evidence is.
    /// </summary>
    public const float MinimumVisualFootprintFraction = 1.0f / VisualFootprintSampleCount;

    /// <summary>
    /// Maximum direct distance solved by one adapter-owned local pathfinding leg. This is deliberately not a maximum
    /// distance at which a Coggent may intend or continue an actor-relative movement. Longer movements are realized
    /// as repeated bounded local legs while the same semantic spatial-relation action remains active.
    /// </summary>
    public const float MaximumLocalPathfindingDistance = DefaultVisualHorizon;

    /// <summary>
    /// Maximum distance accepted by one explicit movement.step action. A step remains a short locomotion primitive,
    /// not a destination-scale navigation request.
    /// </summary>
    public const float MaximumStepDistance = 4.0f;

    /// <summary>
    /// Maximum net progress one direction-only steering action may make before runtime cognition must reassess current
    /// scene evidence. This is an execution-containment horizon, not a destination distance: Station never constructs a
    /// target point at this range and native context steering may deviate locally around collision while preserving the
    /// requested qualitative heading.
    /// </summary>
    public const float MaximumDirectionalSteeringProgress = 2.75f;

    /// <summary>
    /// Compatibility value retained for existing diagnostics/tests. Direction-only continuation now uses the same bounded
    /// progress horizon directly rather than projecting a pseudo-destination at that distance.
    /// </summary>
    public const float BlindContinuationDistance = MaximumDirectionalSteeringProgress;

    /// <summary>
    /// Compatibility value retained for older callers. Moving targets are now replanned incrementally instead of being
    /// failed merely for drifting beyond this distance from their original position.
    /// </summary>
    public const float MaximumTargetDisplacement = 2.5f;

    /// <summary>
    /// Additional travel one local leg may spend on immediate native pathfinding avoidance beyond its direct advance.
    /// </summary>
    public const float LocalDetourAllowance = 2.0f;

    /// <summary>
    /// Maximum direct progress requested from a distant-target leg. Keeping this below the local pathfinding distance
    /// leaves the native pathfinder room for local detours without turning the leg horizon into a total movement cap.
    /// </summary>
    public const float MaximumLocalPathfindingAdvance = MaximumLocalPathfindingDistance - LocalDetourAllowance;

    /// <summary>Maximum execution lifetime for one bounded local pathfinding leg at approximately 60 TPS.</summary>
    public const int MaximumLocalMovementTicks = 480;

    /// <summary>
    /// Chooses how much direct progress one local pathfinding leg should request toward a still-distant semantic target.
    /// A target beyond the leg horizon is advanced toward incrementally rather than rejected as unreachable.
    /// </summary>
    public static float GetLocalPathfindingAdvanceDistance(float remainingDirectTravel)
    {
        if (!float.IsFinite(remainingDirectTravel) || remainingDirectTravel < 0f)
            throw new ArgumentOutOfRangeException(nameof(remainingDirectTravel));

        return remainingDirectTravel <= MaximumLocalPathfindingDistance
            ? remainingDirectTravel
            : MaximumLocalPathfindingAdvance;
    }

    /// <summary>
    /// Computes the travel budget for one local leg. A nearby leg gets a small detour allowance and a distant action
    /// can begin another leg after making bounded progress. No single native pathfinding leg may spend more than the
    /// adapter's local pathfinding distance.
    /// </summary>
    public static float GetMaximumLocalTravelDistance(float legDirectDistance)
    {
        if (!float.IsFinite(legDirectDistance) || legDirectDistance < 0f)
            throw new ArgumentOutOfRangeException(nameof(legDirectDistance));

        return MathF.Min(MaximumLocalPathfindingDistance, legDirectDistance + LocalDetourAllowance);
    }
}
