using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

/// <summary>Self-contained snapshot of a guild's category/channel/role structure - deliberately
/// does NOT capture permission overwrites, members, or messages (matches the scope of Discord's
/// own server templates). Stored as a JSON blob rather than modeled as owned entities/rows so
/// applying a template is just "deserialize and replay", with no separate table lifecycle to
/// keep in sync with the source guild (which may be renamed/deleted/restructured after the
/// snapshot was taken - the template stays valid regardless, since it owns its own copy).</summary>
public class TemplateSnapshot
{
    /// <summary>Without these two, "create a house from a template" would replay household
    /// channels into a guild whose household modules are switched off. Templates captured before
    /// this landed deserialize as Community/None, which Guild.Create reads as "take the preset" -
    /// see its Features assignment.</summary>
    public GuildKind Kind { get; set; } = GuildKind.Community;

    public GuildFeatures Features { get; set; } = GuildFeatures.None;

    public List<TemplateRole> Roles { get; set; } = [];
    public List<TemplateCategory> Categories { get; set; } = [];
    public List<TemplateChannel> UncategorizedChannels { get; set; } = [];

    /// <summary>Null when the source guild had no onboarding configured.</summary>
    public TemplateOnboarding? Onboarding { get; set; }
}

/// <summary>Onboarding as captured in a template. Roles and channels are referenced by NAME, not
/// id: a template outlives the guild it was taken from and is replayed into a brand new guild whose
/// roles and channels have entirely different ids. Names that don't resolve on apply are dropped
/// rather than failing the whole template.</summary>
public class TemplateOnboarding
{
    public bool Enabled { get; set; }
    public string? RulesText { get; set; }
    public OnboardingMode Mode { get; set; } = OnboardingMode.Default;
    public List<string> DefaultChannelNames { get; set; } = [];
    public List<TemplateOnboardingPrompt> Prompts { get; set; } = [];
}

public class TemplateOnboardingPrompt
{
    public string Title { get; set; } = null!;
    public OnboardingPromptType Type { get; set; }
    public bool SingleSelect { get; set; }
    public bool Required { get; set; }
    public bool InOnboarding { get; set; } = true;
    public int Position { get; set; }
    public List<TemplateOnboardingOption> Options { get; set; } = [];
}

public class TemplateOnboardingOption
{
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string? Emoji { get; set; }
    public List<string> RoleNames { get; set; } = [];
    public List<string> ChannelNames { get; set; } = [];
    public int Position { get; set; }
}

public class TemplateRole
{
    public string Name { get; set; } = null!;
    public string Color { get; set; } = "#000000";
    public int Position { get; set; }
    public Permissions Permissions { get; set; } = Permissions.None;
}

public class TemplateCategory
{
    public string Name { get; set; } = null!;
    public int Position { get; set; }
    public List<TemplateChannel> Channels { get; set; } = [];
}

public class TemplateChannel
{
    public string Name { get; set; } = null!;
    public ChannelType Type { get; set; }
    public string? Description { get; set; }
    public int Position { get; set; }
}

public class CreateGuildTemplateParams
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string CreatorUserId { get; set; } = null!;
    public string? SourceGuildId { get; set; }
    public TemplateSnapshot Snapshot { get; set; } = null!;
}

public class GuildTemplate : BaseEntity<GuildTemplate>, IPrefixedEntity
{
    public static string Prefix { get; } = "tmpl";

    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string CreatorUserId { get; set; } = null!;

    /// <summary>Advisory only - the guild this was snapshotted from may since have been renamed,
    /// restructured, or deleted. Never dereferenced when applying the template.</summary>
    public string? SourceGuildId { get; set; }

    public TemplateSnapshot Snapshot { get; set; } = null!;
    public int UsageCount { get; set; }

    public static GuildTemplate Create(CreateGuildTemplateParams parameters)
    {
        var date = DateTime.UtcNow;
        return new GuildTemplate
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            Name = parameters.Name,
            Description = parameters.Description,
            CreatorUserId = parameters.CreatorUserId,
            SourceGuildId = parameters.SourceGuildId,
            Snapshot = parameters.Snapshot,
            UsageCount = 0,
        };
    }
}
