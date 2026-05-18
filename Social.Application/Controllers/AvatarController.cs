using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        
        var uploadedFile = await service.UploadAvatarAsync(file, profileId);


        return Accepted();
    }
}