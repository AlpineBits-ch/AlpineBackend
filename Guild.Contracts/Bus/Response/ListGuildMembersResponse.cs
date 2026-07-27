namespace Guild.Contracts.Bus.Response;

public class ListGuildMembersResponse
{
    public List<GuildMemberSummary> Members { get; set; } = new();
}

public class GuildMemberSummary
{
    public string UserId { get; set; }
    public string? Nickname { get; set; }
    public List<string> RoleIds { get; set; } = new();
    public DateTime JoinedAt { get; set; }
    public bool IsBot { get; set; }

    /// <summary>Maps to Discord's communication_disabled_until on the member object.</summary>
    public DateTimeOffset? MutedUntil { get; set; }
}
