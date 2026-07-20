using Domain;

namespace Isle.Domain.Events.Player;

public class PlayerEvent : DomainEvent
{
    public string Id { get; set; }
    public string SteamId { get; set; }
    public string? UserId { get; set; }
}