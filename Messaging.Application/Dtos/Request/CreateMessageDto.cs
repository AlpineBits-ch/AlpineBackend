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

    /// <summary>Which MlsGroupGeneration of the context this message was encrypted under.</summary>
    public int? MlsGeneration { get; set; }
    public string? SenderDeviceId { get; set; }
    public MessageEncryptionState EncryptionState { get; set; } = MessageEncryptionState.Plain;
    
    // This should be user_ids
    public ICollection<string> Mentions { get; set; } = new List<string>();

    // Role ids mentioned (e.g. @role) - expanded server-side into member ids for
    // unread/mention-count purposes; additive, older clients omitting this keep working.
    public ICollection<string> RoleMentions { get; set; } = new List<string>();
    public bool MentionsEveryone { get; set; }
    public bool MentionsHere { get; set; }

    public ICollection<string> Attachments { get; set; } = new List<string>();

}