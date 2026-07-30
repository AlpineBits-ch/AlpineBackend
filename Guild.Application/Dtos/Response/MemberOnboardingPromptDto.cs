using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary>The "Channels &amp; Roles" view of a prompt: same shape as the admin DTO plus what this
/// member currently has selected, so the client can render the screen from one call.</summary>
public class MemberOnboardingPromptDto
{
    public string Id { get; set; } = null!;
    public string Title { get; set; } = null!;
    public OnboardingPromptType Type { get; set; }
    public bool SingleSelect { get; set; }
    public bool Required { get; set; }
    public bool InOnboarding { get; set; }
    public int Position { get; set; }

    public List<MemberOnboardingPromptOptionDto> Options { get; set; } = [];
}

public class MemberOnboardingPromptOptionDto
{
    public string Id { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string? Emoji { get; set; }
    public List<string> RoleIds { get; set; } = [];
    public List<string> ChannelIds { get; set; } = [];
    public int Position { get; set; }

    public bool Selected { get; set; }
}
