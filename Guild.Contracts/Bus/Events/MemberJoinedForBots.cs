namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Published alongside the new "guild.MemberJoined" SignalR broadcast in InviteEndpoint - kept
/// minimal (just enough to know which member changed) because Bots.Application already has a
/// query (GetGuildMemberRequest) to hydrate the full current member state, the same "hydrate on
/// demand" approach GUILD_CREATE uses rather than trying to carry a full snapshot through every
/// event.
/// </summary>
public class MemberJoinedForBots
{
    public string GuildId { get; set; }
    public string UserId { get; set; }
}
