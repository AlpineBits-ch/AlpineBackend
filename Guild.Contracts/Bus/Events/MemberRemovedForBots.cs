namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Published alongside the existing "guild.MemberBanned"/"guild.MemberKicked"/"guild.MemberLeft"
/// SignalR broadcasts in MemberEndpoint. Discord's own Gateway protocol has a single
/// GUILD_MEMBER_REMOVE event for all three cases - Reason is informational only, carried for
/// any bot-side logging, not used to pick a different dispatch shape.
/// </summary>
public class MemberRemovedForBots
{
    public string GuildId { get; set; }
    public string UserId { get; set; }
    public string Reason { get; set; }
}
