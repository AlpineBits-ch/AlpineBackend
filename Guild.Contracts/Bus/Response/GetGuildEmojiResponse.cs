namespace Guild.Contracts.Bus.Response;

public class GetGuildEmojiResponse
{
    public bool Found { get; set; }
    public string? Name { get; set; }
    public bool Animated { get; set; }
}
