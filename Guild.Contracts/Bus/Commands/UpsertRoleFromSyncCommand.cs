namespace Guild.Contracts.Bus.Commands;

public class UpsertRoleFromSyncCommand
{
    public string GuildId { get; set; }

    /// <summary>Null means create; set means update the existing Echo Role.</summary>
    public string? EchoRoleId { get; set; }

    public string Name { get; set; }
    public string Color { get; set; }
    public int Position { get; set; }
    public ulong Permissions { get; set; }

    /// <summary>The same role metadata <see cref="ImportedRoleDto"/> carries.</summary>
    public bool Hoist { get; set; }

    public bool Mentionable { get; set; } = true;
    public string? IconUrl { get; set; }
    public string? UnicodeEmoji { get; set; }
    public bool IsManaged { get; set; }
    public string? BotUserId { get; set; }
    public string? IntegrationId { get; set; }

    public bool IsEveryoneRole { get; set; }
}

public class UpsertRoleFromSyncResponse
{
    public string EchoId { get; set; }
}

public class DeleteRoleFromSyncCommand
{
    public string GuildId { get; set; }
    public string EchoRoleId { get; set; }
}
