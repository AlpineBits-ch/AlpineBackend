namespace Messaging.Application.Dtos.Request;

public class ConversationPositionDto
{
    public string ConversationId { get; set; }
    public int Position { get; set; }
}

public class ReorderConversationsDto
{
    public List<ConversationPositionDto> Conversations { get; set; } = [];
}
