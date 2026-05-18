namespace Messaging.Application.Dtos.Request;

public class CreateCallRequestDto
{
    public string? ConversationId { get; set; }
    public ICollection<string> Participants { get; set; } = [];
}