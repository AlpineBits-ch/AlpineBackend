namespace Guild.Contracts.Bus.Response;

public class ImportGuildOnboardingResponse
{
    /// <summary>Null when everything applied.</summary>
    public string? ErrorMessage { get; set; }

    public bool OnboardingApplied { get; set; }
    public bool WelcomeScreenApplied { get; set; }
}
