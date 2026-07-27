using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

/// <summary>Shared shape for both MESSAGE_REACTION_ADD and MESSAGE_REACTION_REMOVE.</summary>
public class MessageReactionPayload
{
    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("channel_id")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("message_id")]
    public string MessageId { get; set; } = "";

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; set; }

    [JsonPropertyName("emoji")]
    public EmojiPayload Emoji { get; set; } = null!;
}

public class EmojiPayload
{
    /// <summary>Null for a standard unicode emoji - only set for a guild's own custom emoji,
    /// which this project doesn't have a concept of today.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
