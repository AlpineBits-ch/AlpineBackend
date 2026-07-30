namespace Guild.Application.Dtos.Request;

public class UpdateAutoModConfigDto
{
    public bool Enabled { get; set; }
    public List<string> BlockedWords { get; set; } = [];
    public int? MaxMessagesPerInterval { get; set; }
    public int? IntervalSeconds { get; set; }
}
