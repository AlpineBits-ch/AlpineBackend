using System.Text.Json.Serialization;

namespace Bots.Contracts.Gateway.Payloads;

public class GuildCreatePayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("owner_id")]
    public string OwnerId { get; set; } = "";

    [JsonPropertyName("unavailable")]
    public bool Unavailable { get; set; } = false;

    [JsonPropertyName("member_count")]
    public int MemberCount { get; set; }

    [JsonPropertyName("roles")]
    public List<GatewayRolePayload> Roles { get; set; } = new();

    [JsonPropertyName("channels")]
    public List<GatewayChannelPayload> Channels { get; set; } = new();

    /// <summary>Only the connecting bot's own member object - Discord only requires this without
    /// the privileged GUILD_MEMBERS intent, and that's all v1 hydrates (see GatewayHandshakeService).</summary>
    [JsonPropertyName("members")]
    public List<GatewayMemberPayload> Members { get; set; } = new();
}

/// <summary>
/// Discord's role object, as it appears in GUILD_CREATE's role list and in the standalone
/// GUILD_ROLE_CREATE / GUILD_ROLE_UPDATE dispatches.
/// </summary>
public class GatewayRolePayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("color")]
    public int Color { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }

    /// <summary>Discord serializes the permission bitfield as a decimal string, not a number, so
    /// that a mask wider than 53 bits survives a JavaScript client's JSON parser intact.</summary>
    [JsonPropertyName("permissions")]
    public string Permissions { get; set; } = "0";

    [JsonPropertyName("managed")]
    public bool Managed { get; set; }

    [JsonPropertyName("mentionable")]
    public bool Mentionable { get; set; }

    [JsonPropertyName("hoist")]
    public bool Hoist { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("unicode_emoji")]
    public string? UnicodeEmoji { get; set; }

    [JsonPropertyName("flags")]
    public int Flags { get; set; }

    /// <summary>Omitted entirely for an ordinary role, which is what Discord does - the field's
    /// presence is itself the signal that something owns the role.</summary>
    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GatewayRoleTagsPayload? Tags { get; set; }
}

/// <summary>
/// Discord's role <c>tags</c> object, narrowed to the two tags Echo can populate.
/// </summary>
public class GatewayRoleTagsPayload
{
    [JsonPropertyName("bot_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BotId { get; set; }

    [JsonPropertyName("integration_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IntegrationId { get; set; }
}

/// <summary>Shared by GUILD_CREATE's channel list and standalone CHANNEL_CREATE/UPDATE/DELETE dispatches.</summary>
public class GatewayChannelPayload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }
}

public class GatewayMemberPayload
{
    [JsonPropertyName("user")]
    public DiscordUserPayload User { get; set; } = null!;

    [JsonPropertyName("nick")]
    public string? Nick { get; set; }

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = new();

    [JsonPropertyName("joined_at")]
    public DateTimeOffset JoinedAt { get; set; }

    [JsonPropertyName("deaf")]
    public bool Deaf { get; set; }

    [JsonPropertyName("mute")]
    public bool Mute { get; set; }
}
