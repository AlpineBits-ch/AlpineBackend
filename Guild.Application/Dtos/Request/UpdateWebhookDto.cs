namespace Guild.Application.Dtos.Request;

/// <summary>Partial update - every field is optional and null means "leave alone".</summary>
public class UpdateWebhookDto
{
    public string? Name { get; set; }
    public string? AvatarUrl { get; set; }
    public string? ChannelId { get; set; }
}
