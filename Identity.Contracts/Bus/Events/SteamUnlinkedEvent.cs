namespace Identity.Contracts.Bus.Events;

public class SteamUnlinkedEvent
{
    public string UserId { get; set; }
    public string SteamId { get; set; }
}