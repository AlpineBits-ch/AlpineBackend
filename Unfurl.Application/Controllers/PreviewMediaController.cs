using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Unfurl.Application.Media;

namespace Unfurl.Application.Controllers;

/// <summary>Serves the re-hosted preview images that <c>proxy_url</c> points at.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/previews")]
public class PreviewMediaController(
    IAmazonS3 s3,
    IDistributedCache cache,
    ILogger<PreviewMediaController> logger) : ControllerBase
{
    private static readonly TimeSpan HotCacheLifetime = TimeSpan.FromMinutes(10);

    [HttpGet("media/{hash}")]
    public async Task<IActionResult> GetMedia(string hash, CancellationToken ct)
    {
        // The key is derived, never taken from the caller: a hex string of exactly the right length
        // cannot contain a path separator, so there is no way to walk out of the previews prefix.
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return NotFound();

        var cacheKey = $"unfurl:media:{hash}";

        var hot = await cache.GetAsync(cacheKey, ct);
        if (hot is not null && hot.Length > 0) return Image(hot);

        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = Env.StorageConfiguration.BucketName,
                Key = $"{Env.Unfurl.MediaPrefix}/{hash}.jpg",
            }, ct);

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            await cache.SetAsync(cacheKey, bytes,
                new DistributedCacheEntryOptions { SlidingExpiration = HotCacheLifetime }, ct);

            return Image(bytes);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Normal: the source URL was never successfully processed, or the object aged out.
            return NotFound();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not serve preview media {Hash}", hash);
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Immutable and long-lived, because the key is the hash of the source URL - the bytes behind a
    /// given hash never change, so there is nothing for a client to revalidate.
    /// </summary>
    private FileContentResult Image(byte[] bytes)
    {
        Response.Headers.CacheControl = "public, max-age=604800, immutable";
        return File(bytes, "image/jpeg");
    }
}
