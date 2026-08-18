namespace Guild.Contracts.Bus.Request;

/// <summary>
/// Takes a published wiki, or one of its pages, off the public host on the instance operator's
/// authority rather than the guild's.
///
/// The HTTP publication routes are gated on <c>PublishWikiPublicly</c> inside the guild, which an
/// instance moderator does not hold and should not be given. Publishing puts content on the
/// operator's own domain, so the operator has to be able to take it down without joining the guild
/// that put it there.
/// </summary>
public class ModerationUnpublishWikiRequest
{
    /// <summary>The wiki's published slug, as it appears in the public hostname.</summary>
    public string Slug { get; set; } = "";

    /// <summary>One page's slug, or null to take the whole wiki off the public host.</summary>
    public string? PageSlug { get; set; }

    /// <summary>The staff account that asked, for the guild's own audit log.</summary>
    public string ActorUserId { get; set; } = "";

    /// <summary>Why, in the moderator's words.</summary>
    public string? Reason { get; set; }
}
