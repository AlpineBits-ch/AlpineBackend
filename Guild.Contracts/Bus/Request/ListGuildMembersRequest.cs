namespace Guild.Contracts.Bus.Request;

public class ListGuildMembersRequest
{
    public string GuildId { get; set; }
    public int Limit { get; set; } = 100;
}
