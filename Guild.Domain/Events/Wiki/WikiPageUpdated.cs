using Domain;

namespace Guild.Domain.Events.Wiki;

public class WikiPageUpdated : DomainEvent
{
    public string PageId { get; set; }
    public string GuildId { get; set; }

    /// <summary>Who edited.</summary>
    public string? EditorId { get; set; }
}
