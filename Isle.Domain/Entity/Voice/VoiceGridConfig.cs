namespace Isle.Api.Voice;

public class VoiceGridConfig
{
    public float CellSize { get; init; } = 3000f; // UE units (cm) — tune to voice radius

    // Rough Gateway bounds — recalibrate against real telemetry
    public float MapMinX { get; init; } = -800_000f;
    public float MapMaxX { get; init; } = 250_000f;
    public float MapMinY { get; init; } = -800_000f;
    public float MapMaxY { get; init; } = 250_000f;

    // Minimum movement (UE units) before a position update is worth broadcasting
    public float MovementEpsilon { get; init; } = 25f;
}