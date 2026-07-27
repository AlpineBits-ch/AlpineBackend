using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

/// <summary>Discord's GUILD_MEMBER_ADD is just a member object with guild_id merged in.</summary>
public class GuildMemberAddPayload
{
    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("user")]
    public DiscordUserPayload User { get; set; } = null!;

    [JsonPropertyName("nick")]
    public string? Nick { get; set; }

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = new();

    [JsonPropertyName("joined_at")]
    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>GUILD_MEMBER_UPDATE - same shape as add, plus communication_disabled_until (Discord's
/// real field for a member timeout, which is what this project's mute/unmute maps onto).</summary>
public class GuildMemberUpdatePayload
{
    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("user")]
    public DiscordUserPayload User { get; set; } = null!;

    [JsonPropertyName("nick")]
    public string? Nick { get; set; }

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = new();

    [JsonPropertyName("joined_at")]
    public DateTimeOffset JoinedAt { get; set; }

    [JsonPropertyName("communication_disabled_until")]
    public DateTimeOffset? CommunicationDisabledUntil { get; set; }
}
