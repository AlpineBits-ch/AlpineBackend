namespace Messaging.Application.Dtos.Request;

public class CreateReactionDto
{
    public string ConversationId { get; set; }
    public string Reaction { get; set; }
    
    public string? ChannelId { get; set; }
}