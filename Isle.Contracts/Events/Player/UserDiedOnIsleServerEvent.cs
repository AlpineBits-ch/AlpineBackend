namespace Isle.Contracts.Events.Player;

/// <summary>A player's dino died.</summary>
public class UserDiedOnIsleServerEvent
{
    public string SteamId { get; set; } = string.Empty;

    /// <summary>Dinosaur class path the bridge reported, when it could read one.</summary>
    public string? Species { get; set; }
}
