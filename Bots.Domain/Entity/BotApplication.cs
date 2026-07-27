using Persistence;

namespace Bots.Domain.Entity;

/// <summary>
/// Developer-facing management record for a bot. Its own <see cref="BaseEntity{T}.Id"/> is
/// only ever used for Bots-service CRUD URLs - the bot's actual public identity (OAuth
/// client_id, JWT subject, GuildMember.UserId) is <see cref="BotUserId"/>, which mirrors
/// Discord's own trick of collapsing "application id" and "bot user id" into one value.
/// </summary>
public class BotApplication : BaseEntity<BotApplication>, IPrefixedEntity
{
    public static string Prefix { get; } = "boap";

    public string OwnerUserId { get; init; }
    public string BotUserId { get; init; }
    public string Name { get; set; }
    public string? Description { get; set; }
    public string? IconUrl { get; set; }

    /// <summary>Requested permission bitmask shown to guild admins on the authorize screen.</summary>
    public ulong DefaultPermissions { get; set; }

    public bool IsEnabled { get; set; } = true;
}
