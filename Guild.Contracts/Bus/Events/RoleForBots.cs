using Guild.Contracts.Bus.Response;

namespace Guild.Contracts.Bus.Events;

/// <summary>
/// The role lifecycle, published for Bots.Application to translate into Discord's GUILD_ROLE_CREATE
/// / GUILD_ROLE_UPDATE / GUILD_ROLE_DELETE gateway dispatches.
/// </summary>
public class RoleCreatedForBots
{
    public string GuildId { get; set; } = string.Empty;
    public RoleSnapshot Role { get; set; } = null!;
}

public class RoleUpdatedForBots
{
    public string GuildId { get; set; } = string.Empty;
    public RoleSnapshot Role { get; set; } = null!;
}

/// <summary>Carries only the id: the row is gone by the time this is published, and Discord's
/// GUILD_ROLE_DELETE is likewise just <c>guild_id</c> and <c>role_id</c>.</summary>
public class RoleDeletedForBots
{
    public string GuildId { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
}
