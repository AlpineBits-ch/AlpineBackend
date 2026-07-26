namespace Isle.Contracts.Events.Player;

/// <summary>One player hit another.</summary>
public class PlayerDamagedEvent
{
    public string AttackerSteamId { get; set; } = string.Empty;
    public string VictimSteamId { get; set; } = string.Empty;

    /// <summary>Damage dealt by this exchange, as the bridge reported it.</summary>
    public double Damage { get; set; }

    /// <summary>How many hits the exchange covered; the bridge coalesces rapid swings into one line.</summary>
    public int Swings { get; set; }

    /// <summary>Bridge timestamp (unix ms) for when the damage landed.</summary>
    public long OccurredAt { get; set; }
}
