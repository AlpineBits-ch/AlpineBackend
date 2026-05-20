using Guild.Application.Services;
using Messaging.Domain.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Guild.Application.Controllers;
[ApiController]
[Route("api/v1/guilds/{guildId}")]
public class GuildIconController(GuildThumbnailService thumbnailService, IMessageBus bus, ILogger<GuildIconController> logger) : ControllerBase
{
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