namespace Guild.Domain.Enums;

/// <summary>
/// Who is meant to see a roll. Stored as an integer rather than a Postgres enum: per-recipient
/// visibility will add members here, and a <c>HasPostgresEnum</c> member cannot be added without a
/// migration landing first or the service crashes at startup.
/// </summary>
public enum DiceVisibility
{
    /// <summary>Everybody who can read the channel, and the only value v1 accepts.</summary>
    Public = 0,

    /// <summary>Reserved: only whoever runs the game sees the result.</summary>
    GameMasterOnly = 1,

    /// <summary>Reserved: nobody sees the result until it is revealed, not even the roller.</summary>
    Blind = 2,
}
