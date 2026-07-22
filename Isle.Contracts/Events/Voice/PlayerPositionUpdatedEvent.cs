namespace Isle.Contracts.Events.Voice;

// PlayerId carries the userId (SignalR user identifier). Yaw is facing in degrees.
// Vx/Vy/Vz are velocity in UE units/second; TimestampMs is the server unix-ms of the sample,
// both for client-side extrapolation between the ~1 Hz updates.
public record PlayerPositionUpdatedEvent(string PlayerId, float WorldX, float WorldY, float WorldZ, float Yaw = 0f,
    float Vx = 0f, float Vy = 0f, float Vz = 0f, long TimestampMs = 0);
