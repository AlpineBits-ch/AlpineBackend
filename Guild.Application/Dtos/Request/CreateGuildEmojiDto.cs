namespace Guild.Application.Dtos.Request;

public class CreateGuildEmojiDto
{
    public string Name { get; set; } = null!;
    public bool Animated { get; set; }
}
