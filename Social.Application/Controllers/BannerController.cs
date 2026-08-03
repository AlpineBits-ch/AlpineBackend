using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Social.Api.Services;
using Social.Infrastructure.Persistence;

namespace Social.Api.Controllers;

[ApiController]
[Route("api/v1/profiles/{profileId}")]
public class BannerController(FileService service, MicroserviceContext ctx) : ControllerBase
{
    [HttpGet("banner")]
    public async Task<IActionResult> GetBanner(string profileId)
    {
        var url = await service.GetPresignedUrlForBanner(profileId);
        if (url == null)
            return NotFound();

        return Redirect(url);
    }

    [Authorize]
    [HttpPatch("banner")]
    public async Task<IActionResult> UpdateBanner(string profileId, [FromForm] IFormFile file)
    {
        // Same ownership gate as AvatarController - the route profileId drives the S3 key.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

        var ownsProfile = await ctx.Profiles.AnyAsync(p => p.Id == profileId && p.UserId == userId);
        if (!ownsProfile) return Forbid();

        var invalid = FileService.ValidateImageUpload(file);
        if (invalid is not null) return BadRequest(invalid);

        var uploadedFile = await service.UploadBannerAsync(file, profileId);

        return Accepted();
    }
}
