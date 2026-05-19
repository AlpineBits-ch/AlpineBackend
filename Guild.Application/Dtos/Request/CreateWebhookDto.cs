namespace Guild.Application.Dtos.Request;

public class CreateWebhookDto
{
    public string Name { get; set; } = "Captain Hook";
    public string ChannelId { get; set; }
}