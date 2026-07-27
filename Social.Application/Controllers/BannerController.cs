using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Social.Api.Services;

namespace Social.Api.Controllers;

[ApiController]
[Route("api/v1/profiles/{profileId}")]
public class BannerController(FileService service) : ControllerBase
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
        var uploadedFile = await service.UploadBannerAsync(file, profileId);

        return Accepted();
    }
}
