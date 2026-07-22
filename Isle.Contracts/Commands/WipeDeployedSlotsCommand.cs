namespace Isle.Contracts.Commands;

/// <summary>
/// Fired when a player's dino dies. Any storage slot currently deployed (loaded out) by that player
/// is wiped, since the live dino tied to it is gone. Handled asynchronously off the game-event stream.
/// </summary>
public class WipeDeployedSlotsCommand
{
    public string SteamId { get; set; }
}
