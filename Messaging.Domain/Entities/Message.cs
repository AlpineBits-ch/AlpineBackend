using System.ComponentModel.DataAnnotations.Schema;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Messaging.Domain.Enums;
using Persistence;

namespace Messaging.Domain.Entities;

public class CreateMessageParams
{
    public byte[] Content { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string AuthorId { get; set; }
    public MessageEncryptionState EncryptionState { get; set; } = MessageEncryptionState.Plain;
    public MessageType Type { get; set; } = MessageType.Message;
    public List<string> Mentions { get; set; } = [];
    public long? MlsEpoch { get; set; }
    public long? MlsSequenceNumber { get; set; }
    public string? SenderDeviceId { get; set; }
    public string? InReplyTo { get; set; }
    
    public AuthorIdType AuthorIdType { get; set; } = AuthorIdType.User;
    
    public ICollection<MinimalAttachment> Attachments { get; set; } = new List<MinimalAttachment>();
}
public class Message : BaseEntity<Message>, IPrefixedEntity
{
    public byte[] Content { get; set; }
    public string AuthorId { get; set; }
    public string ContextId { get; set; }
    public MessageEncryptionState EncryptionState { get; set; } 
    public long? MlsEpoch { get; set; }
    public long? MlsSequenceNumber { get; set; }
    public string? SenderDeviceId { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    
    public string? InReplyTo { get; set; }
    
    public MessageType Type { get; set; } = MessageType.Message;
    
    public AuthorIdType AuthorIdType { get; set; } = AuthorIdType.User;
    
    public List<string> Mentions { get; set; } = new();
    public IDictionary<string, DateTimeOffset> ReadReceipts { get; set; } = new Dictionary<string, DateTimeOffset>();
    public ICollection<MinimalAttachment> Attachments { get; set; } = new List<MinimalAttachment>();
    
    public Message()
    {
    }

    [NotMapped] public static string Prefix { get; } = "mesg";

    public static Message Create(CreateMessageParams createMessageParams)
    {       
        
        var contextId = createMessageParams.ConversationId ?? createMessageParams.ChannelId ?? throw new Exception("No context id");
        var id = Message.GenerateId();
        var sentTime = DateTime.UtcNow;
        var message = new Message()
        {
            Id = id,
            CreatedAt = sentTime,
            UpdatedAt = sentTime,
            AuthorId = createMessageParams.AuthorId,
            ContextId = contextId,
            ChannelId = createMessageParams.ChannelId,
            ConversationId = createMessageParams.ConversationId,
            Content = createMessageParams.Content,
            EncryptionState = createMessageParams.EncryptionState,
            Type = createMessageParams.Type,
            Mentions = createMessageParams.Mentions.ToList(),
            Attachments = createMessageParams.Attachments.ToList(),
            InReplyTo = createMessageParams.InReplyTo,
            MlsEpoch = createMessageParams.MlsEpoch,
            MlsSequenceNumber = createMessageParams.MlsSequenceNumber,
            SenderDeviceId = createMessageParams.SenderDeviceId,
            AuthorIdType = createMessageParams.AuthorIdType,
        };
        
        return message;

    }
}