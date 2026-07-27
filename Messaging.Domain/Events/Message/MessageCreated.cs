using Domain;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;

namespace Messaging.Domain.Events.Message;

public class MessageCreated : DomainEvent
{
    public string MessageId { get; set; }
    public byte[] Content { get; set; }
    public string ContextId { get; set; }
    
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    
    public string AuthorId { get; set; }
    
    public string? InReplyTo { get; set; }
    
    public long? MlsEpoch { get; set; }
    public long? MlsSequenceNumber { get; set; }
    public string? SenderDeviceId { get; set; }
    public ICollection<string> Mentions { get; set; } = new List<string>();
    public ICollection<string> RoleMentions { get; set; } = new List<string>();
    public bool MentionsEveryone { get; set; }
    public bool MentionsHere { get; set; }
    public MessageEncryptionState EncryptionState { get; set; } = MessageEncryptionState.Plain;
    public required ICollection<MinimalAttachment> Attachments { get; set; } = new List<MinimalAttachment>();
    public string? EmbedsJson { get; set; }
    public MessageType Type { get; set; } = MessageType.Message;
    public int? SystemMessageVariant { get; set; }
}