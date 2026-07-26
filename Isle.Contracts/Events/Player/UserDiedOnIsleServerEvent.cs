namespace Isle.Contracts.Events.Player;

/// <summary>
/// A player's dino died. Published off the game event stream alongside
/// <see cref="UserJoinedIsleServerEvent"/> / <see cref="UserLeftIsleServerEvent"/>; a death is
/// immediately followed by a respawn, so it doubles as a spawn signal.
/// </summary>
public class UserDiedOnIsleServerEvent
{
    public string SteamId { get; set; } = string.Empty;

    /// <summary>Dinosaur class path the bridge reported, when it could read one.</summary>
    public string? Species { get; set; }
}
