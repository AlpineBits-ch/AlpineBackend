namespace Guild.Domain.Enums;

/// <summary>Mirrors Discord's onboarding mode flag. Advisory only here: Discord uses it to decide
/// whether channels reachable through prompt options count toward its Community-program minimum
/// channel requirements, which have no analogue in this system. Stored so a client can round-trip
/// the setting and show the same toggle.</summary>
public enum OnboardingMode
{
    Default,
    Advanced,
}
