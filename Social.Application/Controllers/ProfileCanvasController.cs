using System.Security.Claims;
using AppEnvironment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Infrastructure.Persistence;

namespace Social.Api.Controllers;

/// <summary>
/// The profile canvas: a four-column grid of user-arranged widgets, plus the images those widgets
/// and the backdrop draw.
/// </summary>
// Routed without the "social" segment, like AvatarController: the gateway inserts one service
// segment after /api/v1, so declaring it here would publish /api/v1/social/social/... and 404.
[ApiController]
[Route("api/v1")]
public class ProfileCanvasController(
    MicroserviceContext ctx,
    ProfileCanvasService canvases,
    ProfileCanvasRealtime realtime,
    FileService files) : ControllerBase
{
    /// <summary>Reads a profile's canvas, stripped to what the caller may see.</summary>
    /// <param name="profileId">The subject profile.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The canvas, or 404 when the profile has never saved one.</returns>
    [Authorize]
    [HttpGet("profiles/{profileId}/canvas")]
    public async Task<IActionResult> GetCanvasAsync(string profileId, CancellationToken token)
    {
        var viewer = await CallerProfileAsync(token);
        if (viewer is null) return Unauthorized();

        var owner = await ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, token);
        if (owner is null) return NotFound();

        // A block in either direction hides the canvas outright rather than showing its public
        // widgets, matching the minimal projection a blocked reader gets from ProfileController.
        if (viewer.Id != owner.Id && await ctx.AnyBlockBetweenAsync(viewer.Id, owner.Id))
            return NotFound();

        var row = await canvases.FindAsync(profileId, token);
        if (row is null) return NotFound();

        var canvas = ProfileCanvasService.ToDto(row);
        var relation = await canvases.ResolveViewerAsync(viewer, owner, canvas.Widgets, token);

        // private, never public: the body is stripped for this caller specifically.
        Response.Headers.CacheControl = "private, max-age=0, must-revalidate";

        return Ok(CanvasVisibility.Strip(canvas, relation));
    }

    /// <summary>Replaces the caller's own canvas.</summary>
    /// <param name="dto">The theme and widget list to store.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The stored canvas with its new version and timestamp.</returns>
    [Authorize]
    [HttpPut("profiles/me/canvas")]
    public async Task<IActionResult> SaveCanvasAsync([FromBody] CanvasWriteDto dto, CancellationToken token)
    {
        // Every write route addresses the owner as "me" and never takes a profile id, so there is
        // no caller-supplied key to overwrite somebody else's canvas with.
        var owner = await CallerProfileAsync(token);
        if (owner is null) return Unauthorized();

        var invalid = ProfileCanvasValidator.Validate(dto);
        if (invalid is not null) return BadRequest(invalid);

        var canvas = await canvases.SaveAsync(owner.Id, dto, token);

        await realtime.PublishAsync(owner, canvas, token);

        return Ok(canvas);
    }

    /// <summary>Uploads an image for the caller's own canvas.</summary>
    /// <param name="file">The image, multipart form field <c>file</c>.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The image id and the URL to fetch it from.</returns>
    [Authorize]
    [HttpPost("profiles/me/canvas/images")]
    public async Task<IActionResult> UploadCanvasImageAsync([FromForm] IFormFile file, CancellationToken token)
    {
        var owner = await CallerProfileAsync(token);
        if (owner is null) return Unauthorized();

        var invalid = FileService.ValidateImageUpload(file);
        if (invalid is not null) return BadRequest(invalid);

        var held = await ctx.ProfileCanvasImages.CountAsync(i => i.ProfileId == owner.Id, token);
        if (held >= ProfileCanvasValidator.MaxImagesPerProfile)
            return BadRequest($"A profile may hold at most {ProfileCanvasValidator.MaxImagesPerProfile} canvas images.");

        var image = ProfileCanvasImage.Create(owner.Id, file.ContentType, file.Length);
        await files.UploadCanvasImageAsync(file, image.Id);

        ctx.ProfileCanvasImages.Add(image);
        await ctx.SaveChangesAsync(token);

        return Ok(new CanvasImageDto { ImageId = image.Id, Url = ImageUrl(image.Id) });
    }

    /// <summary>Deletes one of the caller's own canvas images.</summary>
    /// <param name="imageId">The image to delete.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>204 whether or not the image was still there.</returns>
    [Authorize]
    [HttpDelete("profiles/me/canvas/images/{imageId}")]
    public async Task<IActionResult> DeleteCanvasImageAsync(string imageId, CancellationToken token)
    {
        var owner = await CallerProfileAsync(token);
        if (owner is null) return Unauthorized();

        var image = await ctx.ProfileCanvasImages
            .FirstOrDefaultAsync(i => i.Id == imageId && i.ProfileId == owner.Id, token);
        if (image is null) return NoContent();

        await files.DeleteCanvasImageAsync(imageId);
        ctx.ProfileCanvasImages.Remove(image);
        await ctx.SaveChangesAsync(token);

        await ClearBackdropReferenceAsync(owner, imageId, token);

        return NoContent();
    }

    /// <summary>Redirects to a canvas image.</summary>
    /// <param name="imageId">The image to fetch.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A redirect to the presigned URL, or 404.</returns>
    [HttpGet("canvas-images/{imageId}")]
    public async Task<IActionResult> GetCanvasImageAsync(string imageId, CancellationToken token)
    {
        var exists = await ctx.ProfileCanvasImages.AsNoTracking().AnyAsync(i => i.Id == imageId, token);
        if (!exists) return NotFound();

        var url = await files.GetPresignedUrlForCanvasImage(imageId);
        if (url is null) return NotFound();

        // Public: this route is anonymous, so there is nothing viewer-specific to protect.
        var now = DateTime.UtcNow;
        var maxAge = (int)(FileService.WindowEnd(now) - now).TotalSeconds;
        Response.Headers.CacheControl = $"public, max-age={maxAge}";

        return Redirect(url);
    }

    /// <summary>A deleted image must not stay named by the backdrop, which would render broken.</summary>
    private async Task ClearBackdropReferenceAsync(Profile owner, string imageId, CancellationToken token)
    {
        var row = await canvases.FindAsync(owner.Id, token);
        if (row is null) return;

        var canvas = ProfileCanvasService.ToDto(row);
        if (canvas.Theme.Backdrop?.ImageId != imageId) return;

        canvas.Theme.Backdrop = null;

        var saved = await canvases.SaveAsync(
            owner.Id, new CanvasWriteDto { Theme = canvas.Theme, Widgets = canvas.Widgets }, token);

        await realtime.PublishAsync(owner, saved, token);
    }

    private Task<Profile?> CallerProfileAsync(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Task.FromResult<Profile?>(null);

        return ctx.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, token);
    }

    private static string ImageUrl(string imageId) =>
        $"{Env.GeneralConfiguration.InstanceBaseUrl}/api/v1/social/canvas-images/{imageId}";
}
