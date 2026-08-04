namespace Guild.Contracts.Bus.Response;

public class GetGuildDirectMessagePreferenceResponse
{
    public ICollection<GuildDirectMessagePreferenceSummary> Preferences { get; set; } =
        new List<GuildDirectMessagePreferenceSummary>();
}

/// <summary>
/// The effective answer for one guild, not the raw row: a user with no stored override for a guild
/// still appears here, carrying the value derived from their global <c>DirectMessagePolicy</c>.
/// </summary>
public class GuildDirectMessagePreferenceSummary
{
    public string GuildId { get; set; } = null!;

    public bool AllowDirectMessages { get; set; }
}
