namespace Isle.Contracts.Commands;

public record UpdatePlayerPosition(string PlayerId, float WorldX, float WorldY, float WorldZ = 0f);
public record RemovePlayer(string PlayerId);