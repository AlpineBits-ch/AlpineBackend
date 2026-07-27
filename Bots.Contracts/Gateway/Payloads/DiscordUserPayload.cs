using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

/// <summary>Discord's user object shape, reused for READY.user, message authors, and
/// GUILD_MEMBER_REMOVE.user - every bot in this system is discriminator "0000", Discord's own
/// convention for bot accounts.</summary>
public class DiscordUserPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("discriminator")]
    public string Discriminator { get; set; } = "0000";

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("bot")]
    public bool Bot { get; set; } = true;
}
