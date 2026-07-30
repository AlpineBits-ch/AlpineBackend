namespace Guild.Contracts.Bus.Response;

public class GetGuildOnboardingResponse
{
    /// <summary>False when the caller lacks ManageGuild on the guild (or the guild is unknown) -
    /// the transport has no way to carry a 403, so the endpoint maps this.</summary>
    public bool Authorized { get; set; }

    public GuildOnboardingContract? Config { get; set; }
}

public class UpdateGuildOnboardingResponse
{
    public bool Authorized { get; set; }

    /// <summary>Null on success; otherwise the validation failure to surface as a 400.</summary>
    public string? Error { get; set; }

    public GuildOnboardingContract? Config { get; set; }
}

/// <summary>Transport shape of a guild's onboarding config. Deliberately mirrors the first-party
/// DTO rather than Discord's snake_case wire format - the Bots service owns that translation.</summary>
public class GuildOnboardingContract
{
    public bool Enabled { get; set; }
    public string? RulesText { get; set; }

    /// <summary>0 = Default, 1 = Advanced (Discord's ONBOARDING_DEFAULT / ONBOARDING_ADVANCED).</summary>
    public int Mode { get; set; }

    public List<string> DefaultChannelIds { get; set; } = [];
    public List<GuildOnboardingPromptContract> Prompts { get; set; } = [];
}

public class GuildOnboardingPromptContract
{
    public string? Id { get; set; }
    public string Title { get; set; } = null!;

    /// <summary>0 = MultipleChoice, 1 = Dropdown.</summary>
    public int Type { get; set; }

    public bool SingleSelect { get; set; }
    public bool Required { get; set; }
    public bool InOnboarding { get; set; } = true;
    public int Position { get; set; }
    public List<GuildOnboardingOptionContract> Options { get; set; } = [];
}

public class GuildOnboardingOptionContract
{
    public string? Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string? Emoji { get; set; }
    public List<string> RoleIds { get; set; } = [];
    public List<string> ChannelIds { get; set; } = [];
    public int Position { get; set; }
}
