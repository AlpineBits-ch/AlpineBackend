namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Published alongside the existing "guild.MemberBanned"/"guild.MemberKicked"/"guild.MemberLeft"
/// SignalR broadcasts in MemberEndpoint.
/// </summary>
public class MemberRemovedForBots
{
    public string GuildId { get; set; }
    public string UserId { get; set; }
    public string Reason { get; set; }
}
