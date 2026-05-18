namespace Messaging.Application.Dtos.Response;

public class UserTypingEvent
{
    public string ConversationId { get; set; }
    public string UserId { get; set; }
}