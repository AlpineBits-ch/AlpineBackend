namespace Guild.Contracts.Bus.Request;

/// <summary>
/// Classifies the role ids a message wants to ping against the channel's own guild - used by
/// Messaging's send path, which owns the mention list but has no access to Guild's roles.
/// </summary>
public class ResolveRoleMentionsRequest
{
    public required string ChannelId { get; set; }

    /// <summary>The role ids the client asked to ping, already deduplicated and capped by the
    /// caller.</summary>
    public required IReadOnlyCollection<string> RoleIds { get; set; }

    public override string ToString() =>
        $"ResolveRoleMentionsRequest(ChannelId: {ChannelId}, RoleIds: {RoleIds.Count})";
}
