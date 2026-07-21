namespace Isle.Contracts.Events.Player;

public class EnsurePlayerConnectedToVoiceEvent
{
    public string PlayerId { get; set; }
    public string SteamId { get; set; }
}