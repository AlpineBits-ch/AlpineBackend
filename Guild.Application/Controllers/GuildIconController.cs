using System.Security.Claims;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Messaging.Domain.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Guild.Application.Controllers;
[ApiController]
[Route("api/v1/guilds/{guildId}")]
public class GuildIconController(
    GuildThumbnailService thumbnailService,
    GuildPermissionService permissionService,
    IMessageBus bus,
    ILogger<GuildIconController> logger) : ControllerBase
{
    /// <summary>Image types accepted for a guild icon. The uploaded ContentType is stored on the S3
    /// object and replayed by the presigned GET that the anonymous icon route redirects to.</summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    private const long MaxIconBytes = 8 * 1024 * 1024;

    [HttpGet("icon")]
    public async Task<IActionResult> GetGuildIcon(string guildId)
    {
        var url = await thumbnailService.GetPresignedUrlForIcon(guildId);
        if (string.IsNullOrWhiteSpace(url)) return NotFound();
        return Redirect(url);
    }
    
    [HttpGet("icon/thumbnail")]
    public async Task<IActionResult> GetGuildThumbnail(string guildId)
    {
        var url = await thumbnailService.GetPresignedUrlForThumbnail(guildId);
        if(string.IsNullOrWhiteSpace(url)) return NotFound();
        return Redirect(url);
       
    }

    [Authorize]
    [HttpPost("icon")]
    public async Task<IActionResult> UploadGuildIcon(string guildId, [FromForm] IFormFile file)
    {
        // This controller previously never resolved the caller at all: [Authorize] alone meant any
        // authenticated user could replace the icon of any guild on the instance by putting its id
        // in the route. The upload deletes the existing object before writing the new one, so the
        // original was unrecoverable. GuildEmojiController already gates its equivalent on
        // ManageEmojis; this is the same shape.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        if (!await permissionService.CanUserPerformActionOnGuildAsync(userId, guildId, Permissions.ManageGuild))
            return Forbid();

        if (file is null || file.Length == 0) return BadRequest("A file is required.");
        if (file.Length > MaxIconBytes) return BadRequest($"File exceeds the {MaxIconBytes / (1024 * 1024)}MB limit.");
        if (!AllowedContentTypes.Contains(file.ContentType)) return BadRequest("Unsupported image type.");

        await thumbnailService.UploadIconAsync(file, guildId);
        await bus.SendAsync(new ProcessAttachment()
        {
            AttachmentId = guildId,
            ContentType = file.ContentType,
        });
        
        logger.LogInformation("Uploaded guild icon for guild {GuildId}, mimetype was {MimeType}", guildId, file.ContentType);
        return Accepted();
    }
}