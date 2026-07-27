namespace Guild.Contracts.Bus.Request;

/// <summary>
/// One composite query for everything a Gateway GUILD_CREATE dispatch needs, so a bot connecting to
/// N guilds costs N round-trips (parallelizable), not N*4.
/// </summary>
public class GetGuildSnapshotForBotRequest
{
    public string GuildId { get; set; }
    public string BotUserId { get; set; }
}
