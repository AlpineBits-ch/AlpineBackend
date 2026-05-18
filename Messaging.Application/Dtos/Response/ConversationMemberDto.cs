using Facet;
using Messaging.Domain.Entities;

namespace Messaging.Application.Dtos.Response;

[Facet(typeof(ConversationMember), nameof(ConversationMember.Conversation))]
public partial class ConversationMemberDto
{
    
}