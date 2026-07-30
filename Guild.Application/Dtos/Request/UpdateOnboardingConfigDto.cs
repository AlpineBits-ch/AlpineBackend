using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

/// <summary>Doubles as the GET response - PUT is a whole-document replace, so what you send is
/// what you read back (with server-assigned ids and normalized positions filled in).</summary>
public class UpdateOnboardingConfigDto
{
    public bool Enabled { get; set; }
    public string? RulesText { get; set; }

    public OnboardingMode Mode { get; set; } = OnboardingMode.Default;

    /// <summary>Advisory metadata for the client to highlight - does NOT grant visibility. Channels
    /// are actually unlocked by a prompt option's ChannelIds.</summary>
    public List<string> DefaultChannelIds { get; set; } = [];

    public List<OnboardingPromptDto> Prompts { get; set; } = [];
}

public class OnboardingPromptDto
{
    /// <summary>Null/absent creates a new prompt; an existing id updates in place. Any prompt in
    /// the database whose id is absent from the payload is deleted.</summary>
    public string? Id { get; set; }

    public string Title { get; set; } = null!;
    public OnboardingPromptType Type { get; set; }
    public bool SingleSelect { get; set; }
    public bool Required { get; set; }
    public bool InOnboarding { get; set; } = true;
    public int Position { get; set; }

    public List<OnboardingPromptOptionDto> Options { get; set; } = [];
}

public class OnboardingPromptOptionDto
{
    public string? Id { get; set; }

    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string? Emoji { get; set; }

    public List<string> RoleIds { get; set; } = [];
    public List<string> ChannelIds { get; set; } = [];
    public int Position { get; set; }
}
