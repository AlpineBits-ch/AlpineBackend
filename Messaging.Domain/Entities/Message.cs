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
    public int? SystemMessageVariant { get; set; }
    public List<string> Mentions { get; set; } = [];
    public List<string> RoleMentions { get; set; } = [];
    public bool MentionsEveryone { get; set; }
    public bool MentionsHere { get; set; }
    public long? MlsEpoch { get; set; }
    public long? MlsSequenceNumber { get; set; }
    public string? SenderDeviceId { get; set; }
    public string? InReplyTo { get; set; }
    
    public AuthorIdType AuthorIdType { get; set; } = AuthorIdType.User;

    /// <summary>See Message.AuthorDisplayName - webhook executions only.</summary>
    public string? AuthorDisplayName { get; set; }

    /// <summary>See Message.AuthorAvatarUrl - webhook executions only.</summary>
    public string? AuthorAvatarUrl { get; set; }

    public ICollection<MinimalAttachment> Attachments { get; set; } = new List<MinimalAttachment>();

    /// <summary>Raw JSON array of structured Discord-shaped embeds (see Bots.Contracts'
    /// EmbedPayload), stored opaquely here - same convention as BotCommand.OptionsJson. Null/empty
    /// when the message has no embeds.</summary>
    public string? EmbedsJson { get; set; }

    /// <summary>Raw JSON array of interactive components (see Bots.Contracts' ComponentPayload).
    /// Same opaque-storage convention and same reasoning as EmbedsJson.</summary>
    public string? ComponentsJson { get; set; }
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

    /// <summary>Structured embed data (title/description/fields/etc.), stored as a raw JSON string
    /// rather than modeled as owned entities/UDTs - keeps it storable identically across both the
    /// Scylla (Cassandra text column) and Postgres (EF Core, self-hosted) backing stores without
    /// needing two different mapping strategies. Clients JSON.parse this to render rich cards;
    /// Content still carries a plain-text fallback for any unmodified consumer.</summary>
    public string? EmbedsJson { get; set; }

    /// <summary>Buttons, select menus and their action rows, as the same opaque JSON array the bot
    /// wire format uses (Bots.Contracts' ComponentPayload). Stored rather than modelled for the
    /// reasons on <see cref="EmbedsJson"/>; nothing server-side interprets it beyond checking that
    /// an incoming interaction names a custom_id this array actually contains.</summary>
    public string? ComponentsJson { get; set; }

    public string? InReplyTo { get; set; }
    
    public MessageType Type { get; set; } = MessageType.Message;

    /// <summary>Which of the fixed set of localized copy variants a client should render for
    /// this Type (see SystemMessageVariants) - null for ordinary MessageType.Message.</summary>
    public int? SystemMessageVariant { get; set; }

    public AuthorIdType AuthorIdType { get; set; } = AuthorIdType.User;

    /// <summary>Per-message author name override.</summary>
    public string? AuthorDisplayName { get; set; }

    /// <summary>Companion to <see cref="AuthorDisplayName"/> - the avatar to render for this one
    /// message. Same rules: webhook-only, null otherwise.</summary>
    public string? AuthorAvatarUrl { get; set; }

    public List<string> Mentions { get; set; } = new();
    public List<string> RoleMentions { get; set; } = new();
    public bool MentionsEveryone { get; set; }
    public bool MentionsHere { get; set; }
    public ICollection<MinimalAttachment> Attachments { get; set; } = new List<MinimalAttachment>();

    public bool IsPinned { get; set; }
    public DateTime? PinnedAt { get; set; }
    public string? PinnedById { get; set; }

    public Message()
    {
    }

    [NotMapped] public static string Prefix { get; } = "mesg";

    /// <summary>Explicit column list for reading the "messages" table.</summary>
    public const string SelectColumns =
        "context_id, message_id, author_id, content, created_at, updated_at, in_reply_to, " +
        "sender_device_id, mls_epoch, mls_sequence_number, conversation_id, channel_id, mentions, " +
        "role_mentions, mentions_everyone, mentions_here, author_id_type, message_type, attachments, " +
        "encryption_state, embeds_json, system_message_variant, is_pinned, pinned_at, pinned_by_id, " +
        "author_display_name, author_avatar_url, components_json";

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
            SystemMessageVariant = SystemMessageVariants.PickFor(createMessageParams.Type),
            Mentions = createMessageParams.Mentions.ToList(),
            RoleMentions = createMessageParams.RoleMentions.ToList(),
            MentionsEveryone = createMessageParams.MentionsEveryone,
            MentionsHere = createMessageParams.MentionsHere,
            Attachments = createMessageParams.Attachments.ToList(),
            InReplyTo = createMessageParams.InReplyTo,
            MlsEpoch = createMessageParams.MlsEpoch,
            MlsSequenceNumber = createMessageParams.MlsSequenceNumber,
            SenderDeviceId = createMessageParams.SenderDeviceId,
            AuthorIdType = createMessageParams.AuthorIdType,
            EmbedsJson = createMessageParams.EmbedsJson,
            ComponentsJson = createMessageParams.ComponentsJson,
            AuthorDisplayName = createMessageParams.AuthorDisplayName,
            AuthorAvatarUrl = createMessageParams.AuthorAvatarUrl,
        };
        
        return message;

    }
}