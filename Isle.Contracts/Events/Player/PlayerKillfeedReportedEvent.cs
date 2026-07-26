namespace Isle.Contracts.Events.Player;

/// <summary>
/// Raw killfeed line off the game event stream, still keyed by Steam id. A handler resolves both
/// sides to domain player ids and republishes the resolved <see cref="PlayerKillEvent"/>; anything
/// that needs the unresolved form (players not yet registered) can subscribe here instead.
/// </summary>
public class PlayerKillfeedReportedEvent
{
    public string KillerSteamId { get; set; } = string.Empty;
    public string VictimSteamId { get; set; } = string.Empty;
    public double VictimWeightInKg { get; set; }

    /// <summary>Bridge-supplied dedupe key for the kill, when present.</summary>
    public string? IdempotencyKey { get; set; }
}
