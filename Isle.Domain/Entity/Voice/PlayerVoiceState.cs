namespace Isle.Api.Voice;

public class PlayerVoiceState
{
    public required string PlayerId { get; init; }
    public MapCell CurrentCell { get; set; }

    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }

    // In-game facing in degrees (Unreal yaw); drives client-side directional audio.
    public float Yaw { get; set; }

    // Last position actually broadcast for spatialization — separate from
    // PosX/PosY above, which track raw position for cell-membership purposes.
    public float LastEmittedX { get; set; }
    public float LastEmittedY { get; set; }
    public float LastEmittedZ { get; set; }
    public float LastEmittedYaw { get; set; }
    public bool HasEmittedPosition { get; set; }
}