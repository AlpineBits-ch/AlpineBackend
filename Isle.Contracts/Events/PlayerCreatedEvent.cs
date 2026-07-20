namespace Isle.Contracts.Events;

public class PlayerCreatedEvent
{
    public string Id { get; set; }
    public string SteamId { get; set; }
    
    public string? UserId { get; set; }
}