namespace Guild.Contracts.Bus.Commands;

/// <summary>
/// Bulk-creates a brand-new guild from an already-fetched-and-mapped Discord server structure.
/// </summary>
public class ImportGuildStructureCommand
{
    public string OwnerId { get; set; }
    public string Name { get; set; }
    public string? Description { get; set; }
    public List<ImportedCategoryDto> Categories { get; set; } = [];
    public List<ImportedRoleDto> Roles { get; set; } = [];
}

public class ImportedCategoryDto
{
    /// <summary>Discord snowflake id for this category - echoed back on the response so the
    /// caller can build its Discord-id -&gt; Echo-id mapping.</summary>
    public string DiscordId { get; set; }
    public string Name { get; set; }
    public int Position { get; set; }
    public List<ImportedChannelDto> Channels { get; set; } = [];
}

public class ImportedChannelDto
{
    public string DiscordId { get; set; }
    public string Name { get; set; }

    /// <summary>Guild.Domain.Enums.ChannelType's name, carried as a string since Contracts
    /// projects have no reference to Guild.Domain (same convention as other enums mirrored
    /// across the bus, e.g. AuthorIdType/MessageType).</summary>
    public string Type { get; set; }
    public int Position { get; set; }
    public bool IsAgeRestricted { get; set; }
    public int SlowModeSeconds { get; set; }
    public List<ImportedOverwriteDto> Overwrites { get; set; } = [];
}

public class ImportedOverwriteDto
{
    /// <summary>Discord role id this overwrite targets.</summary>
    public string DiscordRoleId { get; set; }
    public ulong AllowPermissions { get; set; }
    public ulong DenyPermissions { get; set; }
}

public class ImportedRoleDto
{
    public string DiscordId { get; set; }
    public string Name { get; set; }
    public string Color { get; set; }
    public int Position { get; set; }

    /// <summary>Already remapped to Guild.Domain.Enums.Permissions bits by the caller.</summary>
    public ulong Permissions { get; set; }

    /// <summary>Discord's <c>hoist</c>: show this role's members grouped in the member list.</summary>
    public bool Hoist { get; set; }

    /// <summary>Discord's <c>mentionable</c>.</summary>
    public bool Mentionable { get; set; } = true;

    /// <summary>Discord's <c>icon</c>, already resolved from its role-icon hash to a fully qualified
    /// CDN URL by the caller - Guild has no way to turn a hash back into a URL.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Discord's <c>unicode_emoji</c>.</summary>
    public string? UnicodeEmoji { get; set; }

    /// <summary>Discord's <c>managed</c>: the role belongs to an integration (a bot install, a
    /// subscription, a linked role) rather than to a person. Carried across so the imported guild's
    /// admins see the same non-editable roles they saw on Discord, instead of a set of ordinary
    /// roles they can rename and delete while the integration that owns them keeps existing.</summary>
    public bool IsManaged { get; set; }

    /// <summary>Discord's <c>tags.bot_id</c>, when the role is managed because of a bot.</summary>
    public string? BotUserId { get; set; }

    /// <summary>Discord's <c>tags.integration_id</c>, when something other than a bot user owns
    /// it.</summary>
    public string? IntegrationId { get; set; }

    /// <summary>True for Discord's @everyone role - Guild.Create already made one, so the
    /// handler updates its Permissions instead of creating a duplicate.</summary>
    public bool IsEveryoneRole { get; set; }
}

public class ImportGuildStructureResponse
{
    public string? GuildId { get; set; }

    /// <summary>Null on success.</summary>
    public string? ErrorMessage { get; set; }

    public Dictionary<string, string> DiscordToEchoCategoryIds { get; set; } = [];
    public Dictionary<string, string> DiscordToEchoChannelIds { get; set; } = [];
    public Dictionary<string, string> DiscordToEchoRoleIds { get; set; } = [];
}
