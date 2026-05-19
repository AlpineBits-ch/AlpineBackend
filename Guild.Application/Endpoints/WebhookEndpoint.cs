using System.Security.Claims;
using Facet.Extensions.EFCore;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

[Authorize]
public class WebhookEndpoint
{
    [HttpGet("/api/v1/guilds/{guildId}/webhooks")]
    public async Task<IResult> GetWebhooksByGuildAsync(string guildId, [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        if (! await permissionService.CanUserPerformActionOnGuildAsync(user, guildId, Permissions.ManageChannel))
            return Results.Forbid();
        
        
        var webhooks = await ctx.WebhookConfigs.Where(w => w.GuildId == guildId).ToFacetsAsync<WebhookConfig, WebhookConfigDto>();

        return Results.Ok(webhooks);
    }
    
    [HttpPost("/api/v1/guilds/{guildId}/webhooks")]
    public async Task<IResult> CreateWebhookAsync(string guildId, [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        if (! await permissionService.CanUserPerformActionOnGuildAsync(user, guildId, Permissions.ManageChannel))
            return Results.Forbid();
        
        
        var date = DateTime.UtcNow;
        var webhook = new WebhookConfig()
        {
            Id = WebhookConfig.GenerateId(),
            GuildId = guildId,
            CreatedBy = user.FindFirstValue(ClaimTypes.NameIdentifier)!,
            CreatedAt = date,
            UpdatedAt = date,
        };
        
        ctx.WebhookConfigs.Add(webhook);
        
        return Results.Ok(new WebhookConfigDto()
        {
            Id = webhook.Id,
            GuildId = guildId,
            CreatedBy = webhook.CreatedBy,
            CreatedAt = webhook.CreatedAt,
            UpdatedAt = webhook.UpdatedAt,
        });
    }
    
    [HttpDelete("/api/v1/guilds/{guildId}/webhooks/{webhookId}")]
    public async Task<IResult> DeleteWebhookAsync(string webhookId, string guildId, [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        if (! await permissionService.CanUserPerformActionOnGuildAsync(user, guildId, Permissions.ManageChannel))
            return Results.Forbid();
        
        var webhook = ctx.WebhookConfigs.FirstOrDefault(w => w.Id == webhookId && w.GuildId == guildId);
        if (webhook == null) return Results.NotFound();
        
        ctx.WebhookConfigs.Remove(webhook);
        
        return Results.Ok(new WebhookConfigDto()
        {
            Id = webhook.Id,
            GuildId = guildId,
            CreatedBy = webhook.CreatedBy,
            CreatedAt = webhook.CreatedAt,
            UpdatedAt = webhook.UpdatedAt,
        });
    }
}