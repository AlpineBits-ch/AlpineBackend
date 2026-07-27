namespace Guild.Application.Dtos.Request;

public class UpdateChannelDto
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public bool IsAgeRestricted { get; set; }
    public bool IsPrivate { get; set; }
    public int SlowModeSeconds { get; set; }
}
