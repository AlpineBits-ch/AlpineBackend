namespace Guild.Contracts.Bus.Request;

/// <summary>
/// One composite query for everything a Gateway GUILD_CREATE dispatch needs, so a bot connecting
/// to N guilds costs N round-trips (parallelizable), not N*4. BotUserId is included so the
/// handler can also return the bot's own GuildMember row - Discord's GUILD_CREATE only strictly
/// requires the connecting client's own member object without the privileged GUILD_MEMBERS intent.
/// </summary>
public class GetGuildSnapshotForBotRequest
{
    public string GuildId { get; set; }
    public string BotUserId { get; set; }
}
