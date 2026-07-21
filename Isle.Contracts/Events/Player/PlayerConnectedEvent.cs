namespace Isle.Contracts.Events.Player;

public class PlayerConnectedEvent
{
    public string PlayerId { get; set; }
    public string SteamId { get; set; }
    public string? UserId { get; set; }
}