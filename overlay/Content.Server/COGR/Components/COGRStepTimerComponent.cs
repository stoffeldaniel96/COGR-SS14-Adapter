using Robust.Shared.Timing;

namespace Content.Server.COGR.Components;

/// <summary>
/// Temporary component that tracks when to stop a COGR step movement.
/// Used by COGRStepSystem to automatically clear movement after a brief duration.
/// </summary>
[RegisterComponent]
public sealed partial class COGRStepTimerComponent : Component
{
    /// <summary>
    /// The game time when movement should stop.
    /// </summary>
    [DataField]
    public TimeSpan StopTime;
}
