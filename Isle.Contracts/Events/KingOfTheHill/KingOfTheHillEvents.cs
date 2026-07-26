namespace Isle.Contracts.Events.KingOfTheHill;

/// <summary>Published whenever a King of the Hill match starts.</summary>
public class KothMatchStartedEvent
{
    public string DefinitionId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
}

/// <summary>A match resolved — by timeout, or by someone holding the hill alone long enough to win early.</summary>
public class KothMatchResolvedEvent
{
    public string DefinitionId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The top-standing player, or null if nobody registered a single control tick.</summary>
    public string? WinnerPlayerId { get; set; }

    /// <summary>Everyone who actually received a reward, best placing first.</summary>
    public List<string> PaidPlayerIds { get; set; } = [];
}

/// <summary>A match was called off before it resolved (<c>!kothadmin end</c>) — no payout, no <c>GameModeRun</c> row.</summary>
public class KothMatchCancelledEvent
{
    public string DefinitionId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
}
