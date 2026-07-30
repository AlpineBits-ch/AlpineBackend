using Guild.Contracts.Bus.Response;

namespace Guild.Contracts.Bus.Request;

/// <summary>Backs the Discord-compatible GET /guilds/{id}/onboarding. Bots.Application never
/// touches the guild database directly, so the whole config round-trips over the bus.</summary>
public class GetGuildOnboardingRequest
{
    public string GuildId { get; set; } = null!;

    /// <summary>The bot's user id - onboarding config is ManageGuild-gated even for reads, matching
    /// the first-party endpoint and Discord's own permission requirement.</summary>
    public string ActorUserId { get; set; } = null!;
}

/// <summary>Backs the Discord-compatible PUT /guilds/{id}/onboarding. Whole-document replace, same
/// as the first-party endpoint - and validated by the same service, so a bot cannot configure
/// something the UI would have rejected.</summary>
public class UpdateGuildOnboardingRequest
{
    public string GuildId { get; set; } = null!;
    public string ActorUserId { get; set; } = null!;
    public GuildOnboardingContract Config { get; set; } = null!;
}
