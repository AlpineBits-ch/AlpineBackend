using Guild.Contracts.Bus.Response;

namespace Guild.Contracts.Bus.Request;

/// <summary>Applies a Discord guild's onboarding and welcome screen to a freshly-imported Echo
/// guild. Role and channel references must already be remapped to Echo ids by the importer - the
/// guild service validates them and drops anything that doesn't resolve.</summary>
public class ImportGuildOnboardingRequest
{
    public string GuildId { get; set; } = null!;

    /// <summary>The user who requested the import - the owner of the new guild, so permission and
    /// role-hierarchy checks pass, while the privileged-role guard still applies.</summary>
    public string ActorUserId { get; set; } = null!;

    public GuildOnboardingContract? Onboarding { get; set; }
    public ImportedWelcomeScreen? WelcomeScreen { get; set; }
}

public class ImportedWelcomeScreen
{
    public string? Description { get; set; }
    public List<ImportedWelcomeChannel> Channels { get; set; } = [];
}

public class ImportedWelcomeChannel
{
    public string ChannelId { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string? Emoji { get; set; }
}
