using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

public class MessageCreatePayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("channel_id")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("author")]
    public DiscordUserPayload Author { get; set; } = null!;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("mention_everyone")]
    public bool MentionEveryone { get; set; }

    [JsonPropertyName("mentions")]
    public List<DiscordUserPayload> Mentions { get; set; } = new();

    [JsonPropertyName("attachments")]
    public List<object> Attachments { get; set; } = new();

    [JsonPropertyName("embeds")]
    public List<EmbedPayload> Embeds { get; set; } = new();

    [JsonPropertyName("tts")]
    public bool Tts { get; set; }

    [JsonPropertyName("pinned")]
    public bool Pinned { get; set; }

    /// <summary>Discord's numeric message type - none of Echo's own MessageType values (Invite, the
    /// join/leave notices, dice rolls) has a Discord counterpart, so a bot always sees 0 (DEFAULT).</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; } = 0;

    /// <summary>Discord's message flag bitfield.</summary>
    [JsonPropertyName("flags")]
    public int Flags { get; set; }

    /// <summary>When the author last edited the text, or null if they never did.</summary>
    [JsonPropertyName("edited_timestamp")]
    public DateTimeOffset? EditedTimestamp { get; set; }

    /// <summary>Set for webhook executions and persona posts - discord.js reads it into
    /// Message#webhookId and passes !data.webhook_id as the flag deciding whether to cache the
    /// author, so a name override only stays out of its user cache while this is present.</summary>
    [JsonPropertyName("webhook_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebhookId { get; set; }

    /// <summary>Non-standard: which kind of author spoke, one of the DiscordAuthorType values.</summary>
    [JsonPropertyName("author_type")]
    public string AuthorType { get; set; } = DiscordAuthorType.User;

    /// <summary>Non-standard: the character a persona post was spoken as, absent on every other
    /// kind of message.</summary>
    [JsonPropertyName("persona")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessagePersonaPayload? Persona { get; set; }
}

/// <summary>Wire values for <see cref="MessageCreatePayload.AuthorType"/>.</summary>
public static class DiscordAuthorType
{
    public const string User = "user";
    public const string Bot = "bot";
    public const string Webhook = "webhook";
    public const string Persona = "persona";
}

/// <summary>The character behind a persona post, carried next to the standard author object rather
/// than in place of it.</summary>
public class MessagePersonaPayload
{
    /// <summary>The persona's own id, or null for a character that arrived over federation and has
    /// no local row.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>The character's display name, the same string the author object carries.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Absolute URL, matching what author.avatar carries on this surface.</summary>
    [JsonPropertyName("avatar_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AvatarUrl { get; set; }
}

public class MessageDeletePayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("channel_id")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; set; }
}

/// <summary>Dispatched as MESSAGE_DELETE_BULK.</summary>
public class MessageDeleteBulkPayload
{
    [JsonPropertyName("ids")]
    public List<string> Ids { get; set; } = [];

    [JsonPropertyName("channel_id")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; set; }
}
