using Facet;
using Messaging.Domain.Aggregates;

namespace Messaging.Application.Dtos.Response;

[Facet(typeof(Conversation), NestedFacets = [typeof(ConversationMemberDto)])]
public partial class ConversationDto
{
    
}