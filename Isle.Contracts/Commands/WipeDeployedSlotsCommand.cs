namespace Isle.Contracts.Commands;

/// <summary>Fired when a player's dino dies.</summary>
public class WipeDeployedSlotsCommand
{
    public string SteamId { get; set; }
}
