namespace Guild.Domain.Enums;

/// <summary>Purely a rendering hint for the client - both types behave identically server-side,
/// the difference is whether the options are drawn as cards/checkboxes or as a dropdown list.
/// Whether a member may pick more than one is governed by
/// <see cref="Entity.GuildOnboardingPrompt.SingleSelect"/>, not by this.</summary>
public enum OnboardingPromptType
{
    MultipleChoice,
    Dropdown,
}
