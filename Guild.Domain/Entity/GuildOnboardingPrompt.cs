using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

/// <summary>A question shown during onboarding (and/or in the post-join "Channels &amp; Roles"
/// screen) whose options grant roles and unlock channels when picked. Owned by the guild's
/// onboarding config, but modeled as its own table rather than JSON on the config because members'
/// answers reference individual options by id.</summary>
public class GuildOnboardingPrompt : BaseEntity<GuildOnboardingPrompt>, IPrefixedEntity
{
    public static string Prefix { get; } = "onbp";

    public string GuildId { get; set; } = null!;
    public virtual Aggregates.Guild Guild { get; set; } = null!;

    public string Title { get; set; } = null!;
    public OnboardingPromptType Type { get; set; }

    /// <summary>At most one option may be picked.</summary>
    public bool SingleSelect { get; set; }

    /// <summary>The member cannot finish onboarding without answering.</summary>
    public bool Required { get; set; }

    /// <summary>False = the prompt exists only in the post-join "Channels &amp; Roles" screen and
    /// is never part of the join flow.</summary>
    public bool InOnboarding { get; set; } = true;

    /// <summary>Normalized to 0..n-1 on write - client-supplied ordering is honored but not
    /// trusted.</summary>
    public int Position { get; set; }

    public virtual ICollection<GuildOnboardingPromptOption> Options { get; set; } = [];
}

/// <summary>One answer a member can pick.</summary>
public class GuildOnboardingPromptOption : BaseEntity<GuildOnboardingPromptOption>, IPrefixedEntity
{
    public static string Prefix { get; } = "onbo";

    public string PromptId { get; set; } = null!;
    public virtual GuildOnboardingPrompt Prompt { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Unicode emoji or a guild emoji id - same loose convention the client already uses
    /// for reactions, deliberately not validated against GuildEmoji.</summary>
    public string? Emoji { get; set; }

    /// <summary>Roles granted when this option is picked.</summary>
    public List<string> RoleIds { get; set; } = [];

    /// <summary>Channels made visible when this option is picked, via a member-scoped ViewChannel
    /// permission overwrite.</summary>
    public List<string> ChannelIds { get; set; } = [];

    public int Position { get; set; }
}
