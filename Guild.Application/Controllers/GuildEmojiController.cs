using System.Security.Claims;
using Echo.Realtime;
using Facet.Extensions.EFCore;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Guild.Application.Controllers;

// MVC-style controller (matches GuildIconController) rather than Wolverine-HTTP, since emoji
// creation needs a multipart form upload (name + animated flag + image file) in one request.
[Authorize]
[ApiController]
[Route("api/v1/guilds/{guildId}/emojis")]
public class GuildEmojiController(
    MicroserviceContext ctx,
    GuildEmojiService emojiService,
    GuildPermissionService permissionService,
    AuditLogService auditLog,
    GuildHydrateService guildHydrateService,
    IHubContext<EchoRealtimeHub> hub,
    IMessageBus bus) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetEmojis(string guildId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ViewChannel))
            return Forbid();

        var emojis = await ctx.GuildEmojis.Where(e => e.GuildId == guildId)
            .OrderBy(e => e.Name)
            .ToFacetsAsync<GuildEmoji, GuildEmojiDto>();

        foreach (var emoji in emojis)
        {
            emoji.ImageUrl = emojiService.GetPresignedUrl(guildId, emoji.Id);
        }

        return Ok(emojis);
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> CreateEmoji(string guildId, [FromForm] string name, [FromForm] bool animated, [FromForm] IFormFile file)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageEmojis))
            return Forbid();

        if (string.IsNullOrWhiteSpace(name) || file is null || file.Length == 0) return BadRequest();

        var nameTaken = await ctx.GuildEmojis.AnyAsync(e => e.GuildId == guildId && e.Name.ToLower() == name.ToLower());
        if (nameTaken) return Conflict($"An emoji named '{name}' already exists in this guild.");

        var emoji = GuildEmoji.Create(new CreateGuildEmojiParams
        {
            GuildId = guildId,
            Name = name,
            CreatedByUserId = userId,
            Animated = animated,
        });

        await emojiService.UploadEmojiAsync(file, guildId, emoji.Id);
        ctx.GuildEmojis.Add(emoji);

        auditLog.Log(guildId, userId, AuditActionType.EmojiCreated, emoji.Id, new { emoji.Name });

        // MVC controller, not a Wolverine handler - not wrapped by the EF transaction middleware,
        // so this has to commit itself (same reasoning as UserVerificationEndpoint/GuildEndpoint).
        await ctx.SaveChangesAsync();

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.EmojiCreated",
            new { GuildId = guildId, EmojiId = emoji.Id, Name = emoji.Name, Animated = emoji.Animated });

        return Ok(new GuildEmojiDto
        {
            Id = emoji.Id,
            GuildId = guildId,
            Name = emoji.Name,
            Animated = emoji.Animated,
            CreatedByUserId = userId,
            CreatedAt = emoji.CreatedAt,
            ImageUrl = emojiService.GetPresignedUrl(guildId, emoji.Id),
        });
    }

    [Authorize]
    [HttpDelete("{emojiId}")]
    public async Task<IActionResult> DeleteEmoji(string guildId, string emojiId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageEmojis))
            return Forbid();

        var emoji = await ctx.GuildEmojis.FirstOrDefaultAsync(e => e.Id == emojiId && e.GuildId == guildId);
        if (emoji is null) return NotFound();

        ctx.GuildEmojis.Remove(emoji);
        auditLog.Log(guildId, userId, AuditActionType.EmojiDeleted, emojiId, new { emoji.Name });
        await ctx.SaveChangesAsync();

        await emojiService.DeleteEmojiAsync(guildId, emojiId);

        var presence = await guildHydrateService.GetGuildPresenceAsync(guildId);
        await hub.Clients.Users(presence.Select(p => p.UserId)).SendAsync("guild.EmojiDeleted",
            new { GuildId = guildId, EmojiId = emojiId });

        return NoContent();
    }
}
