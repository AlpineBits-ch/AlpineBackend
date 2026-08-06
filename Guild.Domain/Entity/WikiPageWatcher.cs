namespace Guild.Domain.Entity;

public class CreateWikiPageWatcherParams
{
    public string PageId { get; init; }
    public string GuildId { get; init; }
    public string UserId { get; init; }
}

/// <summary>
/// "Tell me when this page changes." Modelled on GuildScheduledEventInterest - the repository's
/// existing user-opts-into-an-object row - down to being a keyless composite join with no
/// BaseEntity and no prefixed id: there is nothing about a watch worth addressing individually.
/// </summary>
public class WikiPageWatcher
{
    public string PageId { get; set; }

    /// <summary>Denormalised from the page, exactly as on WikiPageReaction, so the fan-out on
    /// update does not have to join back to wiki_pages to know which guild to authorize.</summary>
    public string GuildId { get; set; }

    public string UserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public static WikiPageWatcher Create(CreateWikiPageWatcherParams @params) => new()
    {
        PageId = @params.PageId,
        GuildId = @params.GuildId,
        UserId = @params.UserId,
        CreatedAt = DateTime.UtcNow,
    };
}
