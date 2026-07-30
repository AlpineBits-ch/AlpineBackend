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

    [JsonPropertyName("type")]
    public int Type { get; set; } = 0;
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
