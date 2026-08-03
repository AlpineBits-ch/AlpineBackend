using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppEnvironment;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

[Authorize]
public class WebhookEndpoint
{
    /// <summary>
    /// A webhook may only ever target a channel in its own guild. Enforced here rather than relied
    /// upon, because webhook execution is anonymous - the token in the URL is the whole credential -
    /// so whatever channel is stored becomes an unauthenticated write target.
    /// </summary>
    private static async Task<bool> ChannelBelongsToGuildAsync(MicroserviceContext ctx, string? channelId, string guildId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return false;

        return await ctx.Channels
            .AsNoTracking()
            .AnyAsync(c => c.Id == channelId && c.GuildId == guildId);
    }

    [HttpGet("/api/v1/guilds/{guildId}/webhooks")]
    public async Task<IResult> GetWebhooksByGuildAsync(string guildId, [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        if (! await permissionService.CanUserPerformActionOnGuildAsync(user, guildId, Permissions.ManageWebhooks))
            return Results.Forbid();

        var webhooks = await ctx.WebhookConfigs.AsNoTracking().Where(w => w.GuildId == guildId).ToListAsync();

        // Tokens are included: the caller has already proven ManageWebhooks, and a management UI
        // that cannot show the URL is useless. Same call Discord makes.
        return Results.Ok(webhooks.Select(w => WebhookWithTokenDto.From(w, Env.GeneralConfiguration.InstanceUrl)).ToList());
    }

    [HttpGet("/api/v1/guilds/{guildId}/webhooks/{webhookId}")]
    public async Task<IResult> GetWebhookAsync(string guildId, string webhookId, [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        if (! await permissionService.CanUserPerformActionOnGuildAsync(user, guildId, Permissions.ManageWebhooks))
            return Results.Forbid();

        var webhook = await ctx.WebhookConfigs.AsNoTracking().FirstOrDefaultAsync(w => w.Id == webhookId && w.GuildId == guildId);
        if (webhook is null) return Results.NotFound();

        return Results.Ok(WebhookWithTokenDto.From(webhook, Env.GeneralConfiguration.InstanceUrl));
    }

    [HttpPost("/api/v1/guilds/{guildId}/webhooks")]
    public async Task<IResult> CreateWebhookAsync(string guildId, CreateWebhookDto dto, [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        if (! await permissionService.CanUserPerformActionOnGuildAsync(user, guildId, Permissions.ManageWebhooks))
            return Results.Forbid();

        // The permission above is checked against the ROUTE guild; the channel comes from the body.
        // Without tying the two together, a user could create a webhook in a guild they own that
        // points at a private channel in someone else's guild - and since webhook execution is
        // anonymous and only validates the token, that handed them a permanent, unauthenticated
        // write into that channel with an attacker-chosen display name and avatar. The victim guild
        // could not even see the webhook, let alone revoke it, because listing filters on GuildId.
        if (!await ChannelBelongsToGuildAsync(ctx, dto.ChannelId, guildId))
            return Results.BadRequest("Channel must belong to this guild.");

        var webhook = WebhookConfig.Create(
            guildId, dto.ChannelId, user.FindFirstValue(ClaimTypes.NameIdentifier)!, dto.Name, dto.AvatarUrl);

        ctx.WebhookConfigs.Add(webhook);

        return Results.Ok(WebhookWithTokenDto.From(webhook, Env.GeneralConfiguration.InstanceUrl));
    }

    [HttpPatch("/api/v1/guilds/{guildId}/webhooks/{webhookId}")]
    public async Task<IResult> UpdateWebhookAsync(string guildId, string webhookId, UpdateWebhookDto dto, [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        if (! await permissionService.CanUserPerformActionOnGuildAsync(user, guildId, Permissions.ManageWebhooks))
            return Results.Forbid();

        var webhook = await ctx.WebhookConfigs.FirstOrDefaultAsync(w => w.Id == webhookId && w.GuildId == guildId);
        if (webhook is null) return Results.NotFound();

        if (!string.IsNullOrWhiteSpace(dto.Name)) webhook.Name = dto.Name;
        if (dto.AvatarUrl is not null) webhook.AvatarUrl = string.IsNullOrWhiteSpace(dto.AvatarUrl) ? null : dto.AvatarUrl;

        // Re-pointing an existing webhook is the same cross-guild write primitive as creating one -
        // see CreateWebhookAsync.
        if (!string.IsNullOrWhiteSpace(dto.ChannelId))
        {
            if (!await ChannelBelongsToGuildAsync(ctx, dto.ChannelId, guildId))
                return Results.BadRequest("Channel must belong to this guild.");

            webhook.ChannelId = dto.ChannelId;
        }

        webhook.UpdatedAt = DateTimeOffset.UtcNow;

        return Results.Ok(WebhookWithTokenDto.From(webhook, Env.GeneralConfiguration.InstanceUrl));
    }

    /// <summary>Rotates the token. The only way to revoke a leaked webhook URL short of deleting
    /// the webhook, since the id in the URL stays the same.</summary>
    [HttpPost("/api/v1/guilds/{guildId}/webhooks/{webhookId}/regenerate-token")]
    public async Task<IResult> RegenerateTokenAsync(string guildId, string webhookId, [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        if (! await permissionService.CanUserPerformActionOnGuildAsync(user, guildId, Permissions.ManageWebhooks))
            return Results.Forbid();

        var webhook = await ctx.WebhookConfigs.FirstOrDefaultAsync(w => w.Id == webhookId && w.GuildId == guildId);
        if (webhook is null) return Results.NotFound();

        webhook.Regenerate();

        return Results.Ok(WebhookWithTokenDto.From(webhook, Env.GeneralConfiguration.InstanceUrl));
    }

    [HttpDelete("/api/v1/guilds/{guildId}/webhooks/{webhookId}")]
    public async Task<IResult> DeleteWebhookAsync(string webhookId, string guildId, [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        if (! await permissionService.CanUserPerformActionOnGuildAsync(user, guildId, Permissions.ManageWebhooks))
            return Results.Forbid();

        var webhook = ctx.WebhookConfigs.FirstOrDefault(w => w.Id == webhookId && w.GuildId == guildId);
        if (webhook == null) return Results.NotFound();

        ctx.WebhookConfigs.Remove(webhook);

        return Results.Ok(WebhookConfigDto.From(webhook));
    }

    /// <summary>
    /// Execute a webhook. Deliberately [AllowAnonymous]: the token in the path *is* the
    /// credential, exactly like DiscordInteractionEndpoint's callback routes and like Discord's
    /// own webhook execution. That is the whole point - the callers are GitHub, CI and alerting
    /// systems that have no venta account and never will.
    ///
    /// The path shape mirrors Discord's `/api/webhooks/{id}/{token}` so an existing "Discord
    /// webhook" integration works by swapping the host and nothing else.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("/api/webhooks/{webhookId}/{token}")]
    public async Task<IResult> ExecuteWebhook(string webhookId, string token, WebhookRequestDto webhookDto,
        [NotBody] MicroserviceContext ctx, [NotBody] IMessageBus bus)
    {
        var webhook = await ctx.WebhookConfigs.AsNoTracking().FirstOrDefaultAsync(w => w.Id == webhookId);

        // A wrong token is answered with the same 404 as a missing webhook, so the endpoint can't
        // be used to enumerate which webhook ids exist.
        if (webhook is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(webhook.Token), Encoding.UTF8.GetBytes(token)))
        {
            return Results.NotFound();
        }

        var hasContent = !string.IsNullOrWhiteSpace(webhookDto.Content);
        var hasEmbeds = webhookDto.Embeds.Count > 0;
        if (!hasContent && !hasEmbeds) return Results.BadRequest("content or embeds is required");

        // Embeds were previously accepted by the DTO and then silently dropped. They're stored as
        // the same opaque JSON array the bot path already writes (Message.EmbedsJson), so a
        // webhook-posted embed renders through exactly the same client code as a bot-posted one.
        var embedsJson = hasEmbeds
            ? JsonSerializer.Serialize(webhookDto.Embeds.Select(e => new
            {
                title = e.Title,
                description = e.Description,
                url = e.Url,
                color = e.Color,
                fields = e.Fields.Select(f => new { name = f.Name, value = f.Value, inline = f.Inline }),
            }))
            : null;

        // Falls back to a plain-text rendering of the embeds when the caller sent no `content` at
        // all - very common for alerting integrations, and an otherwise-empty message would look
        // broken with no indication why. Same reasoning as Bots' EmbedFlattener.
        var content = hasContent
            ? webhookDto.Content!
            : FlattenEmbeds(webhookDto.Embeds);

        await bus.InvokeAsync(new CreateMessageCommand
        {
            Content = Encoding.UTF8.GetBytes(content),
            ChannelId = webhook.ChannelId,
            Attachments = [],
            // The webhook's own id, so the author is a resolvable entity. The human-readable name
            // travels separately in AuthorDisplayName - previously the caller-supplied username
            // string was jammed into AuthorId, which no client could resolve to anything.
            AuthorId = webhook.Id,
            Mentions = [],
            AuthorIdType = AuthorIdType.Webhook,
            EmbedsJson = embedsJson,
            AuthorDisplayName = string.IsNullOrWhiteSpace(webhookDto.UserName) ? webhook.Name : webhookDto.UserName,
            AuthorAvatarUrl = string.IsNullOrWhiteSpace(webhookDto.AvatarUrl) ? webhook.AvatarUrl : webhookDto.AvatarUrl,
        });

        return Results.NoContent();
    }

    private static string FlattenEmbeds(List<WebhookEmbedDto> embeds)
    {
        var builder = new StringBuilder();
        foreach (var embed in embeds)
        {
            if (builder.Length > 0) builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(embed.Title)) builder.AppendLine($"**{embed.Title}**");
            if (!string.IsNullOrWhiteSpace(embed.Url)) builder.AppendLine(embed.Url);
            if (!string.IsNullOrWhiteSpace(embed.Description)) builder.AppendLine(embed.Description);

            foreach (var field in embed.Fields)
            {
                builder.AppendLine($"{field.Name}: {field.Value}");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
