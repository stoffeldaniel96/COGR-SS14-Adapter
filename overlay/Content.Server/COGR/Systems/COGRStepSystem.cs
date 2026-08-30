using Content.Server.COGR.Components;
using Content.Shared.Movement.Components;
using Robust.Shared.Timing;

namespace Content.Server.COGR.Systems;

/// <summary>
/// Handles automatic stopping of COGR step movements after a brief duration.
/// Works in conjunction with COGRMovementHandler to provide single-step movement.
/// </summary>
public sealed partial class COGRStepSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var curTime = _timing.CurTime;
        var query = EntityQueryEnumerator<COGRStepTimerComponent, InputMoverComponent>();

        while (query.MoveNext(out var uid, out var timer, out var mover))
        {
            // Check if it's time to stop
            if (curTime >= timer.StopTime)
            {
                // Clear the movement vector
                mover.CurTickSprintMovement = System.Numerics.Vector2.Zero;
                mover.LastInputTick = _timing.CurTick;
                mover.LastInputSubTick = ushort.MaxValue;
                Dirty(uid, mover);

                // Remove the timer component
                RemCompDeferred<COGRStepTimerComponent>(uid);
            }
        }
    }
}
