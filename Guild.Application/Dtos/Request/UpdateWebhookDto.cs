namespace Guild.Application.Dtos.Request;

/// <summary>Partial update - every field is optional and null means "leave alone". An explicitly
/// empty <see cref="AvatarUrl"/> is the one exception: it clears the avatar, since there is
/// otherwise no way to remove one once set.</summary>
public class UpdateWebhookDto
{
    public string? Name { get; set; }
    public string? AvatarUrl { get; set; }
    public string? ChannelId { get; set; }
}
