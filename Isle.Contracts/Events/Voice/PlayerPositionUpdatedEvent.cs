namespace Isle.Contracts.Events.Voice;

public record PlayerPositionUpdatedEvent(string PlayerId, float WorldX, float WorldY, float WorldZ);