namespace Guild.Application.Dtos.Request;

/// <summary>Body of POST /onboarding/accept and PUT /onboarding/me/responses.</summary>
public class OnboardingResponsesDto
{
    public List<OnboardingPromptResponseDto> Responses { get; set; } = [];
}

public class OnboardingPromptResponseDto
{
    public string PromptId { get; set; } = null!;
    public List<string> OptionIds { get; set; } = [];
}
