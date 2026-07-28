namespace Guild.Contracts.Bus.Commands;

/// <summary>
/// Applies a single Discord CHANNEL_CREATE/CHANNEL_UPDATE dispatch (categories included - they are
/// just type:4 channels in Discord's own model) to an already-linked/imported guild.
/// </summary>
public class UpsertChannelFromSyncCommand
{
    public string GuildId { get; set; }

    /// <summary>Null means create; set means update the existing Echo entity.</summary>
    public string? EchoCategoryId { get; set; }
    public string? EchoChannelId { get; set; }

    /// <summary>True when this Discord channel is itself a category (type 4) - creates/updates
    /// a Category row instead of a Channel row.</summary>
    public bool IsCategory { get; set; }

    public string Name { get; set; }
    public string? Type { get; set; }
    public string? EchoParentCategoryId { get; set; }
    public int Position { get; set; }
    public bool IsAgeRestricted { get; set; }
    public int SlowModeSeconds { get; set; }

    /// <summary>Unlike ImportGuildStructureCommand's overwrites (which carry a Discord role id
    /// Guild resolves itself in the same bulk call), sync events arrive one at a time long after
    /// roles were created - the caller (Import.Application) must resolve Discord role id -> Echo
    /// role id itself via its own persisted ImportEntityMapping before sending this.</summary>
    public List<SyncOverwriteDto> Overwrites { get; set; } = [];
}

public class SyncOverwriteDto
{
    public string EchoRoleId { get; set; }
    public ulong AllowPermissions { get; set; }
    public ulong DenyPermissions { get; set; }
}

public class UpsertChannelFromSyncResponse
{
    public string EchoId { get; set; }
}

public class DeleteChannelFromSyncCommand
{
    public string GuildId { get; set; }
    public string EchoId { get; set; }
    public bool IsCategory { get; set; }
}
