using Guild.Domain.Enums;

namespace Guild.Domain.Entity;

/// <summary>One-to-one with Guild, upserted like GuildAutoModConfig - "the config" always exists
/// conceptually, defaulting to disabled.</summary>
public class GuildOnboardingConfig
{
    public string GuildId { get; set; } = null!;
    public virtual Aggregates.Guild Guild { get; set; } = null!;

    public bool Enabled { get; set; }
    public string? RulesText { get; set; }

    /// <summary>Advisory - see OnboardingMode. Stored so clients can round-trip Discord's toggle.</summary>
    public OnboardingMode Mode { get; set; } = OnboardingMode.Default;

    // Prompts hang off the guild (GuildOnboardingPrompt.GuildId), not off this row - the config is
    // keyed on GuildId too, and giving both entities a relationship over the same column just to
    // get a navigation property here buys nothing over querying prompts by guild id directly.

    /// <summary>Channels highlighted to new members during onboarding (Discord's "default
    /// channels" concept) - purely advisory metadata for the client to render, not a permission
    /// grant (visibility is still governed by the normal channel/role permission system).</summary>
    public List<string> DefaultChannelIds { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
