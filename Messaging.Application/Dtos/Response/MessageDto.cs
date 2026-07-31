using Facet;
using Messaging.Domain.Entities;

namespace Messaging.Application.Dtos.Response;

[Facet(typeof(Message), NestedFacets = [typeof(MinimalAttachmentDto)])]
public partial class MessageDto
{
    /// <summary>Not produced by the Facet mapping - reactions live in their own table (and their
    /// own Scylla column) and are fetched alongside the page, so the message endpoints fill this
    /// in per message.</summary>
    public ICollection<ReactionDto> Reactions { get; set; } = new List<ReactionDto>();
}

/// <summary>The attachment stub carried on a message. Scalar-only, so it is safe to nest.</summary>
[Facet(typeof(MinimalAttachment))]
public partial class MinimalAttachmentDto
{

}

/// <summary>
/// <see cref="Reaction"/> has no navigations today, so it cannot loop on its own - but it is a
/// mapped EF entity, and a response shape that hands tracked entities to the serializer is one
/// navigation away from breaking.
/// </summary>
[Facet(typeof(Reaction))]
public partial class ReactionDto
{

}
