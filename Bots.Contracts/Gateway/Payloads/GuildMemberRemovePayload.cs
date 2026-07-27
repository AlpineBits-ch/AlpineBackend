using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

/// <summary>Discord's single GUILD_MEMBER_REMOVE shape - covers ban, kick, and voluntary leave alike.</summary>
public class GuildMemberRemovePayload
{
    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("user")]
    public DiscordUserPayload User { get; set; } = null!;
}
