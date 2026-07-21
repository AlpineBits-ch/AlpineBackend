namespace Isle.Contracts.Events.Player;

public class PlayerDisconnectedEvent
{
    public string PlayerId { get; set; }
    public string SteamId { get; set; }
}