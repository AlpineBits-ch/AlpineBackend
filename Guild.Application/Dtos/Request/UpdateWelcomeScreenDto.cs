namespace Guild.Application.Dtos.Request;

/// <summary>Doubles as the GET response and as the `welcomeScreen` field on the invite preview.</summary>
public class UpdateWelcomeScreenDto
{
    public bool Enabled { get; set; }
    public string? Description { get; set; }
    public List<WelcomeChannelDto> Channels { get; set; } = [];
}

public class WelcomeChannelDto
{
    public string ChannelId { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string? Emoji { get; set; }
    public int Position { get; set; }
}
