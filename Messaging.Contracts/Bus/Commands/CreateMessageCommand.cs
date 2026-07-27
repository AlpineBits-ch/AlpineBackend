namespace Messaging.Contracts.Bus.Commands;

public class CreateMessageCommand
{
    public AuthorIdType AuthorIdType { get; set; } = AuthorIdType.User;
    public string AuthorId { get; set; }
    public byte[] Content { get; set; }
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
    public string? InReplyTo { get; set; }
    public long? MlsEpoch { get; set; }
    public long? MlsSequenceNumber { get; set; }
    public string? SenderDeviceId { get; set; }
    public MessageEncryptionState EncryptionState { get; set; } = MessageEncryptionState.Plain;

    /// <summary>Raw JSON array of Discord-shaped embeds - see Messaging.Domain.Entities.Message.EmbedsJson.</summary>
    public string? EmbedsJson { get; set; }

    public List<string> Mentions { get; set; } = new List<string>();
    public List<string> RoleMentions { get; set; } = new List<string>();
    public bool MentionsEveryone { get; set; }
    public bool MentionsHere { get; set; }
    public List<MinimalAttachmentContract> Attachments { get; set; } = new List<MinimalAttachmentContract>();
}

public enum AuthorIdType
{
    User,
    Bot,
    Webhook
}

public enum MessageEncryptionState
{
    Plain,
    Encrypted
}

public record MinimalAttachmentContract
{
    public string FileName { get; init; }
    public string ContentType { get; init; }
    public string? ThumbnailUrl { get; init; } = "";
    public string? ThumbnailId { get; init; }
    public string Id { get; set; }
};