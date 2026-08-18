namespace Guild.Contracts.Bus.Response;

/// <summary>What a moderator's takedown found and did.</summary>
public class ModerationUnpublishWikiResponse
{
    /// <summary>False when no published wiki or page answers on that address.</summary>
    public bool Found { get; set; }

    /// <summary>False when it was already off the public host, which is not a failure.</summary>
    public bool Unpublished { get; set; }

    /// <summary>The guild that published it, so the console can act on the account behind it.</summary>
    public string? GuildId { get; set; }

    public string? GuildName { get; set; }

    /// <summary>The page's title, when a single page was taken down.</summary>
    public string? PageTitle { get; set; }
}
