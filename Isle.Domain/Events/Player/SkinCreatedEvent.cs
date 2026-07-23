namespace Isle.Domain.Events.Player;

public class SkinCreatedEvent : PlayerEvent
{
    public string SkinId { get; set; }
}