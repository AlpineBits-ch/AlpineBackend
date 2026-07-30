using Guild.Contracts.Bus.Response;

namespace Guild.Contracts.Bus.Request;

/// <summary>Backs the Discord-compatible GET /guilds/{id}/onboarding.</summary>
public class GetGuildOnboardingRequest
{
    public string GuildId { get; set; } = null!;

    /// <summary>The bot's user id - onboarding config is ManageGuild-gated even for reads, matching
    /// the first-party endpoint and Discord's own permission requirement.</summary>
    public string ActorUserId { get; set; } = null!;
}

/// <summary>Backs the Discord-compatible PUT /guilds/{id}/onboarding.</summary>
public class UpdateGuildOnboardingRequest
{
    public string GuildId { get; set; } = null!;
    public string ActorUserId { get; set; } = null!;
    public GuildOnboardingContract Config { get; set; } = null!;
}
