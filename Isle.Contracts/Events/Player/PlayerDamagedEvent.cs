namespace Isle.Contracts.Events.Player;

/// <summary>
/// One player hit another. Published off the game event stream, still keyed by Steam id — the same
/// treatment <see cref="PlayerKillfeedReportedEvent"/> gets, and for the same reason: the attacker or
/// victim may not be a registered player yet, so resolution is a subscriber's problem.
///
/// <para>This is the loudest event the bridge emits — every swing of every fight on the server — so
/// ingestion drops the lines that carry no attribution (no attacker, self-damage, zero damage) before
/// they ever reach the bus. What survives is player-on-player damage that somebody could be credited
/// for, which is what the bounty participation ledger is built on.</para>
/// </summary>
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
