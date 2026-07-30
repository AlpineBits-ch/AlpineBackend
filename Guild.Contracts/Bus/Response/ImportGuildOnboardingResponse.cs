namespace Guild.Contracts.Bus.Response;

public class ImportGuildOnboardingResponse
{
    /// <summary>Null when everything applied. A failure here does not fail the surrounding import -
    /// the importer logs it and leaves the guild without onboarding.</summary>
    public string? ErrorMessage { get; set; }

    public bool OnboardingApplied { get; set; }
    public bool WelcomeScreenApplied { get; set; }
}
