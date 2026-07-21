namespace Isle.Contracts.Commands;

// PlayerId carries the caller's userId (the SignalR user identifier), despite the name.
// Yaw is the in-game facing in degrees (Unreal convention); 0 when the game omits rotation.
public record UpdatePlayerPositionCommand(string PlayerId, float WorldX, float WorldY, float WorldZ = 0f, float Yaw = 0f);
public record RemovePlayerCommand(string PlayerId);