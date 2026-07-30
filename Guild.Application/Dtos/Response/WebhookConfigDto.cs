using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

/// <summary>
/// Explicit property list rather than a whole-entity Facet (which is what this was before
/// WebhookConfig had a token): <see cref="WebhookConfig.Token"/> is a standing write credential,
/// and a Facet that mirrors the entity would have started returning it from every endpoint the
/// moment it was added to the model.
/// </summary>
public class WebhookConfigDto
{
    public string Id { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? AvatarUrl { get; set; }
    public WebhookType Type { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static WebhookConfigDto From(WebhookConfig webhook) => new()
    {
        Id = webhook.Id,
        GuildId = webhook.GuildId,
        ChannelId = webhook.ChannelId,
        CreatedBy = webhook.CreatedBy,
        Name = webhook.Name,
        AvatarUrl = webhook.AvatarUrl,
        Type = webhook.Type,
        CreatedAt = webhook.CreatedAt,
        UpdatedAt = webhook.UpdatedAt,
    };
}

/// <summary>
/// The token-bearing variant, returned only from the ManageWebhooks-gated management endpoints.
/// </summary>
public class WebhookWithTokenDto : WebhookConfigDto
{
    public string Token { get; set; } = null!;

    public string Url { get; set; } = null!;

    public static WebhookWithTokenDto From(WebhookConfig webhook, string instanceBaseUrl) => new()
    {
        Id = webhook.Id,
        GuildId = webhook.GuildId,
        ChannelId = webhook.ChannelId,
        CreatedBy = webhook.CreatedBy,
        Name = webhook.Name,
        AvatarUrl = webhook.AvatarUrl,
        Type = webhook.Type,
        CreatedAt = webhook.CreatedAt,
        UpdatedAt = webhook.UpdatedAt,
        Token = webhook.Token,
        Url = $"{instanceBaseUrl.TrimEnd('/')}/api/webhooks/{webhook.Id}/{webhook.Token}",
    };
}
