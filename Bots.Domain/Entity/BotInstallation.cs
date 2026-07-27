using Persistence;

namespace Bots.Domain.Entity;

/// <summary>
/// Bookkeeping row for a bot installed into a guild. The actual <c>GuildMember</c> lives in
/// the Guild service - this is only for the Bots service's own "which guilds is my bot in"
/// and "who installed it" views.
/// </summary>
public class BotInstallation : BaseEntity<BotInstallation>, IPrefixedEntity
{
    public static string Prefix { get; } = "bins";

    public string BotApplicationId { get; init; }
    public string GuildId { get; init; }
    public string InstalledByUserId { get; init; }
    public ulong GrantedPermissions { get; set; }
    public string GuildMemberId { get; init; }
    public DateTimeOffset InstalledAt { get; init; }
}
