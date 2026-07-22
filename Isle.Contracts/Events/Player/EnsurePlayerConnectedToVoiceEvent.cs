namespace Isle.Contracts.Events.Player;

public class EnsurePlayerConnectedToVoiceEvent
{
    public string PlayerId { get; set; }
    public string SteamId { get; set; }

    /// <summary>When the voice grace period ends; if still not connected by then the player is kicked.</summary>
    public DateTimeOffset KickAt { get; set; }
}