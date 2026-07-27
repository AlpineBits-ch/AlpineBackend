using Persistence;

namespace Bots.Domain.Entity;

/// <summary>A registered slash (CHAT_INPUT) command definition.</summary>
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
