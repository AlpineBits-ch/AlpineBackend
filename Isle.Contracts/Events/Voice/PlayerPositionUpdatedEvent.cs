namespace Isle.Contracts.Events.Voice;

// PlayerId carries the userId (SignalR user identifier). Yaw is facing in degrees.
public record PlayerPositionUpdatedEvent(string PlayerId, float WorldX, float WorldY, float WorldZ, float Yaw = 0f);
