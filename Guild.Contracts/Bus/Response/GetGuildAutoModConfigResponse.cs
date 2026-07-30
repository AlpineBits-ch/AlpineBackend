namespace Guild.Contracts.Bus.Response;

public class GetGuildAutoModConfigResponse
{
    public string? GuildId { get; set; }
    public bool Enabled { get; set; }
    public List<string> BlockedWords { get; set; } = [];
    public int? MaxMessagesPerInterval { get; set; }
    public int? IntervalSeconds { get; set; }
}
