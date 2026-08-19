using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

/// <summary>Discord represents a thread as a channel object (type 11 = PUBLIC_THREAD) with a
/// nested thread_metadata block - used for both THREAD_CREATE and THREAD_UPDATE.</summary>
public class ThreadPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public int Type { get; set; } = 11;

    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("parent_id")]
    public string ParentId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("thread_metadata")]
    public ThreadMetadataPayload ThreadMetadata { get; set; } = null!;

    /// <summary>Non-standard: the message this thread was started from. Discord conveys the same
    /// fact by giving the thread the starter message's id, which Echo cannot do because ids are
    /// prefixed per entity.</summary>
    [JsonPropertyName("starter_message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StarterMessageId { get; set; }
}

public class ThreadMetadataPayload
{
    [JsonPropertyName("archived")]
    public bool Archived { get; set; }
}
