using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Social.Api.Services;
using Social.Infrastructure.Persistence;

namespace Social.Api.Controllers;

[ApiController]
[Route("api/v1/profiles/{profileId}")]
public class AvatarController(FileService service, MicroserviceContext ctx) : ControllerBase
{
    [HttpGet("avatar")]

    public async Task<IActionResult> GetAvatar(string profileId)
    {
        var url = await service.GetPresignedUrlForAvatar(profileId);
        if (url == null)
            return NotFound();
        
        return Redirect(url);
    }
    [Authorize]

    [HttpPatch("avatar")]
    public async Task<IActionResult> UpdateAvatar(string profileId, [FromForm] IFormFile file)
    {
        // The route profileId is caller-supplied and the S3 key is derived from it, so without
        // this check any authenticated user could delete-then-overwrite any other user's avatar
        // (the delete precedes the put, so the original was unrecoverable).
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var ownsProfile = await ctx.Profiles.AnyAsync(p => p.Id == profileId && p.UserId == userId);
        if (!ownsProfile) return Forbid();

        var invalid = FileService.ValidateImageUpload(file);
        if (invalid is not null) return BadRequest(invalid);

        var uploadedFile = await service.UploadAvatarAsync(file, profileId);


        return Accepted();
    }
}