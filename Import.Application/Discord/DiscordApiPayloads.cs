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

/// <summary>GET /guilds/{id}/onboarding.</summary>
public class DiscordOnboardingPayload
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>0 = ONBOARDING_DEFAULT, 1 = ONBOARDING_ADVANCED.</summary>
    [JsonPropertyName("mode")]
    public int Mode { get; set; }

    [JsonPropertyName("default_channel_ids")]
    public List<string> DefaultChannelIds { get; set; } = [];

    [JsonPropertyName("prompts")]
    public List<DiscordOnboardingPromptPayload> Prompts { get; set; } = [];
}

public class DiscordOnboardingPromptPayload
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>0 = MULTIPLE_CHOICE, 1 = DROPDOWN.</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("single_select")]
    public bool SingleSelect { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("in_onboarding")]
    public bool InOnboarding { get; set; } = true;

    [JsonPropertyName("options")]
    public List<DiscordOnboardingOptionPayload> Options { get; set; } = [];
}

public class DiscordOnboardingOptionPayload
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("emoji")]
    public DiscordEmojiPayload? Emoji { get; set; }

    [JsonPropertyName("role_ids")]
    public List<string> RoleIds { get; set; } = [];

    [JsonPropertyName("channel_ids")]
    public List<string> ChannelIds { get; set; } = [];
}

public class DiscordEmojiPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>GET /guilds/{id}/welcome-screen. 404s for guilds that never configured one.</summary>
public class DiscordWelcomeScreenPayload
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("welcome_channels")]
    public List<DiscordWelcomeChannelPayload> WelcomeChannels { get; set; } = [];
}

public class DiscordWelcomeChannelPayload
{
    [JsonPropertyName("channel_id")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("emoji_name")]
    public string? EmojiName { get; set; }
}
