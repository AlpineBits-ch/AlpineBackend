using System.Text.Json.Serialization;

namespace Guild.Application.Dtos.Request;

/// <summary>Body of an execute-webhook call.</summary>
public class WebhookRequestDto
{
    /// <summary>Per-execution display-name override.</summary>
    [JsonPropertyName("username")]
    public string? UserName { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    public string? Content { get; set; }

    public List<WebhookEmbedDto> Embeds { get; set; } = [];
}

public class WebhookEmbedDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }

    /// <summary>Discord sends this as a decimal integer, venta's stored embeds keep it as a string
    /// (see Bots.Contracts' EmbedPayload.Color). Typed as string and left as whatever arrived -
    /// rendering is the client's decision and both spellings round-trip.</summary>
    public string? Color { get; set; }

    public List<WebhookEmbedFieldDto> Fields { get; set; } = [];
}

public class WebhookEmbedFieldDto
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Inline { get; set; }
}
