using Facet;
using Messaging.Domain.Entities;

namespace Messaging.Application.Dtos.Response;

[Facet(typeof(ConversationMember), nameof(ConversationMember.Conversation),
    NestedFacets = [typeof(ConversationMemberDeviceDto)])]
public partial class ConversationMemberDto
{

}

/// <summary>
/// A member's MLS device - the leaf index a client needs to address it - without the
/// <c>ConversationMember</c> back-reference that would loop the member straight back into itself.
/// </summary>
[Facet(typeof(ConversationMemberDevice), nameof(ConversationMemberDevice.ConversationMember))]
public partial class ConversationMemberDeviceDto
{

}
