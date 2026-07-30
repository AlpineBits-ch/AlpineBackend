using Persistence;

namespace Guild.Domain.Entity;

/// <summary>One-to-one with Guild, upserted like GuildOnboardingConfig. The splash shown to someone
/// looking at an invite - i.e. before they are a member - which is why it is surfaced on the invite
/// preview endpoint rather than only behind guild membership.</summary>
public class GuildWelcomeScreen
{
    public const int MaxChannels = 5;
    public const int MaxDescriptionLength = 140;
    public const int MaxChannelDescriptionLength = 50;

    public string GuildId { get; set; } = null!;
    public virtual Aggregates.Guild Guild { get; set; } = null!;

    public bool Enabled { get; set; }
    public string? Description { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public virtual ICollection<GuildWelcomeChannel> Channels { get; set; } = [];
}

public class GuildWelcomeChannel : BaseEntity<GuildWelcomeChannel>, IPrefixedEntity
{
    public static string Prefix { get; } = "wlcm";

    public string GuildId { get; set; } = null!;
    public virtual GuildWelcomeScreen WelcomeScreen { get; set; } = null!;

    public string ChannelId { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string? Emoji { get; set; }
    public int Position { get; set; }
}
