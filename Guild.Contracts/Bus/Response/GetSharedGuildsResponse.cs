namespace Guild.Contracts.Bus.Response;

public class GetSharedGuildsResponse
{
    /// <summary>
    /// One entry per requested user who shares at least one guild. A user with none is
    /// <b>omitted</b> rather than returned with an empty list - the same convention as
    /// <see cref="GetGuildDirectMessagePreferenceResponse"/>, and it keeps the response
    /// proportional to the answer rather than to the question.
    /// </summary>
    public ICollection<SharedGuildsSummary> Shared { get; set; } = new List<SharedGuildsSummary>();
}

/// <summary>
/// The guilds one pair has in common. Ids only: Guild does not project names here, because a name
/// is a display value and this contract is an authorization input - a caller that needs to render
/// the guild has to be able to see it anyway, and can ask for it by id.
/// </summary>
public class SharedGuildsSummary
{
    public string OtherUserId { get; set; } = null!;

    /// <summary>Never empty - see <see cref="GetSharedGuildsResponse.Shared"/>.</summary>
    public ICollection<string> GuildIds { get; set; } = new List<string>();
}
