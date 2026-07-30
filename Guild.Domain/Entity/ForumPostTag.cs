namespace Guild.Domain.Entity;

/// <summary>Join row between a forum post (a Channel of type Thread) and a <see cref="ForumTag"/>.
///
/// A join table rather than a denormalized text[] on the channel (the shape WikiPage.Tags uses):
/// forum tags are renamable, recolorable, deletable entities, so an array of ids would strand
/// orphans on delete with no FK to catch them, and per-tag post counts would mean scanning every
/// post. Both FKs cascade, so deleting a tag or a post cleans up its applications for free.
///
/// Composite PK (ThreadChannelId, TagId) doubles as the uniqueness constraint - a tag can't be
/// applied to the same post twice - and as the index for "which tags does this post carry".
/// The separate index on TagId serves the inverse, "which posts carry this tag", which is the
/// filter path.</summary>
public class ForumPostTag
{
    /// <summary>The post - a Channel with Type == Thread parented to a Forum/Media channel.</summary>
    public string ThreadChannelId { get; set; } = null!;

    public string TagId { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Matches Discord's cap on applied_tags.</summary>
    public const int MaxTagsPerPost = 5;
}
