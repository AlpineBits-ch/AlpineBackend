using Discovery.Domain.Topics;
using Persistence;

namespace Discovery.Domain.Entities;

public class ListingTopic : BaseEntity<ListingTopic>, IPrefixedEntity
{
    public static string Prefix { get; } = "lstt";

    public string ListingId { get; set; } = null!;
    public virtual Listing Listing { get; set; } = null!;

    public TopicKind Kind { get; set; }

    /// <summary>A `gapp_` id for a game, a slug for a tag.</summary>
    public string TopicId { get; set; } = null!;

    public static ListingTopic For(string listingId, TopicRef topic) =>
        new() { Id = GenerateId(), ListingId = listingId, Kind = topic.Kind, TopicId = topic.Id };
}
