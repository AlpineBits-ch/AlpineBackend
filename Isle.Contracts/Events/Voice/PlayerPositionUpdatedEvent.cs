namespace Isle.Contracts.Events.Voice;

// PlayerId carries the userId (SignalR user identifier).
public record PlayerPositionUpdatedEvent(string PlayerId, float WorldX, float WorldY, float WorldZ, float Yaw = 0f,
    float Vx = 0f, float Vy = 0f, float Vz = 0f, long TimestampMs = 0);
