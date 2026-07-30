using Guild.Domain.Aggregates;

namespace Guild.Domain.Entity;

/// <summary>One-to-one with Guild - a single row per guild, upserted rather than created via a
/// factory (unlike most entities here) since "the config" always exists conceptually even before
/// anyone has ever configured it (defaults = disabled, no rules).</summary>
public class GuildAutoModConfig
{
    public string GuildId { get; set; } = null!;
    public virtual Aggregates.Guild Guild { get; set; } = null!;

    public bool Enabled { get; set; }

    /// <summary>Case-insensitive, whole-word match against message content.</summary>
    public List<string> BlockedWords { get; set; } = [];

    /// <summary>Null disables rate limiting even when Enabled is true for the word filter.</summary>
    public int? MaxMessagesPerInterval { get; set; }
    public int? IntervalSeconds { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
