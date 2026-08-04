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

    /// <summary>
    /// Discord's message flag bitfield. The bit that matters today is SUPPRESS_EMBEDS (1 &lt;&lt; 2),
    /// set when someone dismissed the message's link previews.
    ///
    /// <para>A bot needs it to tell "the previews were dismissed" apart from "this message never
    /// had any" - both arrive as an empty <c>embeds</c> array otherwise. Discord sends the same bit
    /// with the same meaning, so a Discord bot library reads it unmodified.</para>
    /// </summary>
    [JsonPropertyName("flags")]
    public int Flags { get; set; }

    /// <summary>
    /// When the author last edited the text, or null if they never did.
    ///
    /// <para>Null on the MESSAGE_UPDATE that merely attached a link preview, deliberately: that
    /// update is the server adding a card, not the author rewriting anything, and a bot that logs
    /// edits should not report one.</para>
    /// </summary>
    [JsonPropertyName("edited_timestamp")]
    public DateTimeOffset? EditedTimestamp { get; set; }
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

/// <summary>Dispatched as MESSAGE_DELETE_BULK. Note that a bulk delete also emits one ordinary
/// MESSAGE_DELETE per message, matching how the rest of the pipeline treats each removal
/// individually - a bot listening to both will see each id twice, which is why discord.js's
/// messageDeleteBulk handler is the one to use for this event rather than counting deletes.</summary>
public class MessageDeleteBulkPayload
{
    [JsonPropertyName("ids")]
    public List<string> Ids { get; set; } = [];

    [JsonPropertyName("channel_id")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; set; }
}
