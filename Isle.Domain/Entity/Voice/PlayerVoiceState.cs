namespace Isle.Domain.Entity.Voice;

public class PlayerVoiceState
{
    public required string PlayerId { get; init; }
    public MapCell CurrentCell { get; set; }

    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }

    // In-game facing in degrees (Unreal yaw); drives client-side directional audio.
    public float Yaw { get; set; }

    // Velocity in UE units/second, derived from the delta between the last two position samples.
    public float VelX { get; set; }
    public float VelY { get; set; }
    public float VelZ { get; set; }

    // Server unix time (ms) of the last position sample - the reference the client
    // extrapolates from, and the divisor for the velocity derivation above.
    public long LastUpdateUnixMs { get; set; }

    // Last position actually broadcast for spatialization - separate from
    // PosX/PosY above, which track raw position for cell-membership purposes.
    public float LastEmittedX { get; set; }
    public float LastEmittedY { get; set; }
    public float LastEmittedZ { get; set; }
    public float LastEmittedYaw { get; set; }
    public bool HasEmittedPosition { get; set; }
}