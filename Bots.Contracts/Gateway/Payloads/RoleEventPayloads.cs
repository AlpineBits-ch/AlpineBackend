using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

/// <summary>The body of a GUILD_ROLE_CREATE or GUILD_ROLE_UPDATE dispatch.</summary>
public class GuildRoleUpdatePayload
{
    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("role")]
    public GatewayRolePayload Role { get; set; } = null!;
}

/// <summary>
/// The body of a GUILD_ROLE_DELETE dispatch: the id and nothing else, matching Discord.
/// </summary>
public class GuildRoleDeletePayload
{
    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("role_id")]
    public string RoleId { get; set; } = "";
}
