using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

/// <summary>
/// Self-contained snapshot of a guild's category/channel/role structure and the role-targeted
/// permission overwrites on it - deliberately does NOT capture members, messages, or
/// member-targeted overwrites (matches the scope of Discord's own server templates, whose
/// serialized_source_guild carries channel permission_overwrites and nothing about members).
/// </summary>
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

/// <summary>Onboarding as captured in a template.</summary>
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

/// <summary>A role as captured in a template.</summary>
public class TemplateRole
{
    public string Name { get; set; } = null!;

    /// <summary>Absent from snapshots captured before this field existed, which read back as null -
    /// indistinguishable from a role that genuinely had no description, and harmless either way.</summary>
    public string? Description { get; set; }

    public string Color { get; set; } = "#000000";

    /// <summary>The role's rank in the source guild.</summary>
    public int Position { get; set; }

    public Permissions Permissions { get; set; } = Permissions.None;

    /// <summary>The <see cref="Enums.ModulePermissions"/> half of the role's grant.</summary>
    public ModulePermissions ModulePermissions { get; set; } = ModulePermissions.None;

    public bool Hoist { get; set; }

    /// <summary>
    /// Nullable purely so that "absent from an old snapshot" and "captured as false" stay
    /// distinguishable.
    /// </summary>
    public bool? Mentionable { get; set; }

    /// <summary>See the remarks on this class for why the emoji badge is templated and the uploaded
    /// icon is not.</summary>
    public string? UnicodeEmoji { get; set; }

    /// <summary>True for the snapshot's @everyone entry.</summary>
    public bool IsEveryone { get; set; }
}

/// <summary>A role-targeted permission overwrite on a captured channel or category.</summary>
public class TemplateOverwrite
{
    /// <summary>The name of the role this overwrite targets.</summary>
    public string RoleName { get; set; } = null!;

    public Permissions Allow { get; set; }
    public Permissions Deny { get; set; }
    public ModulePermissions AllowModule { get; set; }
    public ModulePermissions DenyModule { get; set; }
}

public class TemplateCategory
{
    public string Name { get; set; } = null!;
    public int Position { get; set; }
    public List<TemplateChannel> Channels { get; set; } = [];

    /// <summary>Empty for snapshots captured before overwrites were templated, which replays as a
    /// category with no overwrites - exactly what those templates produced before.</summary>
    public List<TemplateOverwrite> Overwrites { get; set; } = [];
}

public class TemplateChannel
{
    public string Name { get; set; } = null!;
    public ChannelType Type { get; set; }
    public string? Description { get; set; }
    public int Position { get; set; }

    /// <summary>See <see cref="TemplateCategory.Overwrites"/>.</summary>
    public List<TemplateOverwrite> Overwrites { get; set; } = [];
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
