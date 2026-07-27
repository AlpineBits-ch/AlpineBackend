namespace Guild.Contracts.Bus.Request;

/// <summary>Resolves a single member's guild-scoped info (nickname, roles, joined-at) - used to
/// populate a Discord Interaction's `member` field when a slash command is invoked.</summary>
public class GetGuildMemberRequest
{
    public string GuildId { get; set; }
    public string UserId { get; set; }
}
