namespace Guild.Application.Dtos.Request;

public class CreateWebhookDto
{
    public string Name { get; set; } = "Captain Hook";
    public string ChannelId { get; set; }

    /// <summary>Default avatar for this webhook's messages; each execution may override it.</summary>
    public string? AvatarUrl { get; set; }
}