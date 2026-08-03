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

    /// <summary>The message's stored <see cref="Entities.Message.CreatedAt"/>, not the time a
    /// consumer happened to receive this event. Guild denormalizes it onto the channel row, so
    /// taking UtcNow downstream instead would drift by the broker latency and put the channel head
    /// slightly ahead of the message the cursor reads resolve against.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public string? InReplyTo { get; set; }
    
    public long? MlsEpoch { get; set; }
    public long? MlsSequenceNumber { get; set; }

    /// <summary>Which MlsGroupGeneration of the context this message was encrypted under. Null on
    /// plaintext messages. Required to decrypt: encryption can be toggled off and on, and each
    /// stretch is a distinct group whose epochs restart at zero, so the epoch alone is ambiguous.</summary>
    public int? MlsGeneration { get; set; }
    public string? SenderDeviceId { get; set; }
    public ICollection<string> Mentions { get; set; } = new List<string>();
    public ICollection<string> RoleMentions { get; set; } = new List<string>();
    public bool MentionsEveryone { get; set; }
    public bool MentionsHere { get; set; }
    public MessageEncryptionState EncryptionState { get; set; } = MessageEncryptionState.Plain;
    public required ICollection<MinimalAttachment> Attachments { get; set; } = new List<MinimalAttachment>();
    public string? EmbedsJson { get; set; }
    public string? ComponentsJson { get; set; }
    public MessageType Type { get; set; } = MessageType.Message;
    public int? SystemMessageVariant { get; set; }
}