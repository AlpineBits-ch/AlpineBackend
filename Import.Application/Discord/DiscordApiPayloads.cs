using System.Text.Json.Serialization;

namespace Import.Application.Discord;

/// <summary>
/// Shapes of real Discord API v10 JSON responses (REST) and Gateway dispatch payloads this service
/// needs.
/// </summary>
public class DiscordGuildPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class DiscordRolePayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Discord returns this as a plain int (0xRRGGBB), unlike permissions.</summary>
    [JsonPropertyName("color")]
    public int Color { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }

    /// <summary>A 53+ bit permission bitmask - Discord serializes it as a decimal string to
    /// avoid JS Number precision loss, so this must be parsed with ulong.Parse, not read as a
    /// JSON number.</summary>
    [JsonPropertyName("permissions")]
    public string Permissions { get; set; } = "0";
}

public class DiscordChannelPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    [JsonPropertyName("nsfw")]
    public bool Nsfw { get; set; }

    [JsonPropertyName("rate_limit_per_user")]
    public int? RateLimitPerUser { get; set; }

    [JsonPropertyName("permission_overwrites")]
    public List<DiscordOverwritePayload> PermissionOverwrites { get; set; } = [];

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; set; }
}

public class DiscordOverwritePayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>0 = role, 1 = member.</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("allow")]
    public string Allow { get; set; } = "0";

    [JsonPropertyName("deny")]
    public string Deny { get; set; } = "0";
}

public class DiscordRoleDispatchPayload
{
    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("role")]
    public DiscordRolePayload Role { get; set; } = new();
}

public class DiscordRoleDeletePayload
{
    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("role_id")]
    public string RoleId { get; set; } = "";
}
