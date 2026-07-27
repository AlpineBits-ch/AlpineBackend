using Persistence;

namespace Bots.Domain.Entity;

/// <summary>
/// A registered slash (CHAT_INPUT) command definition. Mirrors Discord's real application
/// command registration model: a bot bulk-overwrites its global commands, and/or registers
/// commands scoped to one guild (GuildId set) which take effect immediately there instead of
/// waiting on Discord's real ~1hr global-command propagation delay (not something we need to
/// simulate - there's no client-side cache to invalidate on our side).
/// </summary>
public class BotCommand : BaseEntity<BotCommand>, IPrefixedEntity
{
    public static string Prefix { get; } = "boco";

    public string BotApplicationId { get; init; }
    public string Name { get; set; }
    public string Description { get; set; }

    /// <summary>Raw JSON for Discord's option-schema array - deliberately not modeled as typed
    /// entities, matching how BotApplication.DefaultPermissions stores a raw bitmask instead of
    /// normalized rows. Only ever round-tripped whole, never queried into.</summary>
    public string OptionsJson { get; set; } = "[]";

    /// <summary>Null = global command; set = scoped to one guild.</summary>
    public string? GuildId { get; set; }
}
