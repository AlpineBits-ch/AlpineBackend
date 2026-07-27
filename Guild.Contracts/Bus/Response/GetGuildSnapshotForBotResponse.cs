namespace Guild.Contracts.Bus.Response;

public class GetGuildSnapshotForBotResponse
{
    public GuildSnapshot? Guild { get; set; }
}

public class GuildSnapshot
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string OwnerId { get; set; }
    public List<ChannelSnapshot> Channels { get; set; } = new();
    public List<RoleSnapshot> Roles { get; set; } = new();

    /// <summary>The connecting bot's own member row - always present if the bot is actually
    /// installed in this guild. Discord's GUILD_CREATE only strictly requires this without the
    /// privileged GUILD_MEMBERS intent, so no other members are included.</summary>
    public GuildMemberSummary? Self { get; set; }
}

public class ChannelSnapshot
{
    public string Id { get; set; }
    public string Name { get; set; }

    /// <summary>Guild.Domain.Enums.ChannelType's enum name (Text/Voice/Forum/Ticket/Announcement/Thread) -
    /// Guild.Contracts has no project reference to Guild.Domain, so this stays a plain string;
    /// the consumer maps it to Discord's numeric channel type.</summary>
    public string Type { get; set; }
    public int Position { get; set; }
    public string? CategoryId { get; set; }
}

public class RoleSnapshot
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Color { get; set; }
    public int Position { get; set; }
    public ulong Permissions { get; set; }
}
