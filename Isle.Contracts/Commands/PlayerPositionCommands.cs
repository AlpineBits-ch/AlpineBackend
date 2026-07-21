namespace Isle.Contracts.Commands;

public record UpdatePlayerPositionCommand(string PlayerId, float WorldX, float WorldY, float WorldZ = 0f);
public record RemovePlayerCommand(string PlayerId);