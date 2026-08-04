namespace Guild.Contracts.Bus.Response;

public class GetSharedGuildsResponse
{
    /// <summary>One entry per requested user who shares at least one guild.</summary>
    public ICollection<SharedGuildsSummary> Shared { get; set; } = new List<SharedGuildsSummary>();
}

/// <summary>The guilds one pair has in common.</summary>
public class SharedGuildsSummary
{
    public string OtherUserId { get; set; } = null!;

    /// <summary>Never empty - see <see cref="GetSharedGuildsResponse.Shared"/>.</summary>
    public ICollection<string> GuildIds { get; set; } = new List<string>();
}
