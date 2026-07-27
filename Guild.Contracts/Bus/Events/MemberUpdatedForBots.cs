namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Published whenever a member's roles or mute (timeout) state changes - covers both cases
/// Discord's single GUILD_MEMBER_UPDATE event represents.
/// </summary>
public class MemberUpdatedForBots
{
    public string GuildId { get; set; }
    public string UserId { get; set; }
}
