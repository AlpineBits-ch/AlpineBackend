namespace Guild.Application.Dtos.Request;

public class CreateScheduledEventDto
{
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public string? Location { get; set; }
    public string? VoiceChannelId { get; set; }
}

public class UpdateScheduledEventDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public string? Location { get; set; }
    public string? VoiceChannelId { get; set; }
}
