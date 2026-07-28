namespace Guild.Contracts.Bus.Commands;

/// <summary>
/// Bulk-creates a brand-new guild from an already-fetched-and-mapped Discord server structure.
/// Everything Discord-specific (permission bit remap, channel-type remap, which overwrites are
/// role-targeted vs member-targeted) is resolved by the caller (Import.Application) before this
/// crosses the bus - Guild stays entirely Discord-agnostic, matching the existing convention
/// that compat/mapping logic lives in the calling service, not here.
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
    /// <summary>Discord role id this overwrite targets. Member-targeted overwrites are dropped
    /// by the caller before this DTO is built - no member exists yet to attach them to.</summary>
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

    /// <summary>True for Discord's @everyone role - Guild.Create already made one, so the
    /// handler updates its Permissions instead of creating a duplicate.</summary>
    public bool IsEveryoneRole { get; set; }
}

public class ImportGuildStructureResponse
{
    public string GuildId { get; set; }
    public Dictionary<string, string> DiscordToEchoCategoryIds { get; set; } = [];
    public Dictionary<string, string> DiscordToEchoChannelIds { get; set; } = [];
    public Dictionary<string, string> DiscordToEchoRoleIds { get; set; } = [];
}
