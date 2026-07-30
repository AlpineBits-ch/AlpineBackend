using Persistence;

namespace Guild.Domain.Entity;

/// <summary>What a member picked. Kept separate from <see cref="GuildOnboardingGrant"/> because
/// the two answer different questions: this one is "what is selected" (drives the Channels &amp;
/// Roles UI and survives an admin editing the option), the grant table is "what did we actually
/// hand out" (drives revocation).</summary>
public class GuildMemberOnboardingResponse
{
    public string MemberId { get; set; } = null!;
    public virtual GuildMember Member { get; set; } = null!;

    public string OptionId { get; set; } = null!;
    public virtual GuildOnboardingPromptOption Option { get; set; } = null!;

    public string PromptId { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A single thing onboarding actually gave a member - one role, or one channel-visibility
/// overwrite - recorded at the moment it was granted.
///
/// Revocation only ever deletes rows in this table, which is what keeps two failure modes from
/// happening: a role a moderator assigned by hand is never stripped just because the member
/// deselected an option that happens to reference the same role, and an admin editing an option's
/// RoleIds after the fact can't make us revoke a role we never granted.</summary>
public class GuildOnboardingGrant : BaseEntity<GuildOnboardingGrant>, IPrefixedEntity
{
    public static string Prefix { get; } = "onbg";

    public string GuildId { get; set; } = null!;

    public string MemberId { get; set; } = null!;
    public virtual GuildMember Member { get; set; } = null!;

    /// <summary>The option that caused this grant. Not a foreign key: the option may be deleted by
    /// a later config edit, and the grant must outlive it (deleting an option deliberately does not
    /// take back what it already handed out).</summary>
    public string OptionId { get; set; } = null!;

    /// <summary>Exactly one of RoleId / ChannelId is set.</summary>
    public string? RoleId { get; set; }

    public string? ChannelId { get; set; }

    /// <summary>The ChannelPermission row created for a channel grant, so revocation deletes
    /// precisely the overwrite onboarding added rather than matching on shape.</summary>
    public string? ChannelPermissionId { get; set; }

    public static GuildOnboardingGrant ForRole(string guildId, string memberId, string optionId, string roleId)
    {
        var date = DateTimeOffset.UtcNow;
        return new GuildOnboardingGrant
        {
            Id = GenerateId(), CreatedAt = date, UpdatedAt = date,
            GuildId = guildId, MemberId = memberId, OptionId = optionId, RoleId = roleId,
        };
    }

    public static GuildOnboardingGrant ForChannel(string guildId, string memberId, string optionId,
        string channelId, string channelPermissionId)
    {
        var date = DateTimeOffset.UtcNow;
        return new GuildOnboardingGrant
        {
            Id = GenerateId(), CreatedAt = date, UpdatedAt = date,
            GuildId = guildId, MemberId = memberId, OptionId = optionId,
            ChannelId = channelId, ChannelPermissionId = channelPermissionId,
        };
    }
}
