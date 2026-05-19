namespace Guild.Application.Dtos.Request;

public class WebhookRequestDto
{
    public string UserName { get; set; }
    public string AvatarUrl { get; set; }
    public string Content { get; set; }
    public List<WebhookEmbedDto> Embeds { get; set; } = [];
}

public class WebhookEmbedDto
{
    public string Title { get; set; }
    public string Description { get; set; }
    public string Url { get; set; }
    public string Color { get; set; }
    public List<WebhookEmbedFieldDto> Fields { get; set; } = [];
}

public class WebhookEmbedFieldDto
{
    public string Name { get; set; }
    public string Value { get; set; }
    public bool Inline { get; set; }
}