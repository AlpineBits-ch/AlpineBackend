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

    /// <summary>Guild.Domain.Enums.GuildKind's enum name - same string-not-enum convention as
    /// ChannelSnapshot.Type, since Guild.Contracts has no reference to Guild.Domain.</summary>
    public string Kind { get; set; } = "Community";

    /// <summary>Guild.Domain.Enums.GuildFeatures as a raw bitmask, so a bot can avoid advertising
    /// commands for modules the guild doesn't have.</summary>
    public ulong Features { get; set; }

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

/// <summary>
/// A role as the bot gateway sees it: the handshake's GUILD_CREATE role list and the standalone
/// GUILD_ROLE_CREATE/UPDATE dispatches share this one shape, so a bot cannot observe a role
/// differently depending on whether it arrived at connect time or as a live event.
/// </summary>
public class RoleSnapshot
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Color { get; set; }
    public int Position { get; set; }

    /// <summary>The core mask.</summary>
    public ulong Permissions { get; set; }

    /// <summary>Display this role's members in their own member-list group.</summary>
    public bool Hoist { get; set; }

    /// <summary>Whether members without MentionEveryone may @mention this role.</summary>
    public bool Mentionable { get; set; }

    /// <summary>True when an integration owns the role - a bot install today.</summary>
    public bool Managed { get; set; }

    /// <summary>The role badge image, or null.</summary>
    public string? IconUrl { get; set; }

    /// <summary>The single-emoji alternative to <see cref="IconUrl"/>, or null.</summary>
    public string? UnicodeEmoji { get; set; }

    /// <summary>The bot user this role was created for, when an install owns it.</summary>
    public string? BotUserId { get; set; }

    /// <summary>The non-bot integration that owns this role.</summary>
    public string? IntegrationId { get; set; }
}
