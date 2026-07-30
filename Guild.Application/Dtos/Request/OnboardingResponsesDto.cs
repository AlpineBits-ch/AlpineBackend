namespace Guild.Application.Dtos.Request;

/// <summary>Body of POST /onboarding/accept and PUT /onboarding/me/responses. Always a full
/// replacement of the member's picks - a prompt omitted entirely means "nothing selected".</summary>
public class OnboardingResponsesDto
{
    public List<OnboardingPromptResponseDto> Responses { get; set; } = [];
}

public class OnboardingPromptResponseDto
{
    public string PromptId { get; set; } = null!;
    public List<string> OptionIds { get; set; } = [];
}
