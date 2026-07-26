namespace Isle.Contracts.Events.Player;

/// <summary>Raw killfeed line off the game event stream, still keyed by Steam id.</summary>
public class PlayerKillfeedReportedEvent
{
    public string KillerSteamId { get; set; } = string.Empty;
    public string VictimSteamId { get; set; } = string.Empty;
    public double VictimWeightInKg { get; set; }

    /// <summary>Bridge-supplied dedupe key for the kill, when present.</summary>
    public string? IdempotencyKey { get; set; }
}
