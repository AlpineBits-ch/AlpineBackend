using Messaging.Domain.Enums;

namespace Messaging.Application.Dtos.Request;

public class CreateMessageDto
{
    public string Content { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string? InReplyTo { get; set; }
    public long? MlsEpoch { get; set; }
    public long? MlsSequenceNumber { get; set; }
    public string? SenderDeviceId { get; set; }
    public MessageEncryptionState EncryptionState { get; set; } = MessageEncryptionState.Plain;
    
    
    // This should be user_ids
    public ICollection<string> Mentions { get; set; } = new List<string>();
    public ICollection<string> Attachments { get; set; } = new List<string>();
}