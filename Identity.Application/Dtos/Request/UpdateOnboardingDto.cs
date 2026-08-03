namespace Identity.Application.Dtos.Request;

/// <summary>
/// The onboarding picker's answer: which halves of the product this account came for.
/// </summary>
public class UpdateOnboardingDto
{
    public string[]? Interests { get; set; }
}
