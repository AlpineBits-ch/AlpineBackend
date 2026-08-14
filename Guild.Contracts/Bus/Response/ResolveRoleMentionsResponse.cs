namespace Guild.Contracts.Bus.Response;

/// <summary>The requested role ids, split by what the caller has to do about them.</summary>
public class ResolveRoleMentionsResponse
{
    /// <summary>Roles of this channel's guild that are flagged mentionable.</summary>
    public IReadOnlyCollection<string> MentionableRoleIds { get; set; } = [];

    /// <summary>Roles of this channel's guild that are not flagged mentionable.</summary>
    public IReadOnlyCollection<string> RestrictedRoleIds { get; set; } = [];
}
