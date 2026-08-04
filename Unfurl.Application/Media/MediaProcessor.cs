using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;
using Bots.Contracts.Gateway.Payloads;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Unfurl.Application.Fetching;

namespace Unfurl.Application.Media;

/// <summary>
/// Re-hosts a page's preview image and measures it.
///
/// <para><b>Why re-host at all</b> - the alternative is putting the origin's URL straight into the
/// embed, which means every person who scrolls past the message makes a request to that origin.
/// That hands a third party the IP address, rough location and read time of everyone in the
/// channel, from a link one person posted. Serving our own copy is the entire reason Discord's
/// <c>proxy_url</c> exists, and it is also what lets us guarantee the bytes are actually an image.</para>
///
/// <para>Same pipeline as <c>ProcessAttachmentHandler</c> in Messaging - fetch, ImageSharp, S3 -
/// with two additions the attachment path does not need: a megapixel ceiling, because a
/// decompression bomb is tiny on the wire and enormous once decoded, and a thumbhash so clients
/// have something to render before the image arrives.</para>
/// </summary>
public class MediaProcessor(
    SafeFetcher fetcher,
    IAmazonS3 s3,
    ILogger<MediaProcessor> logger)
{
    /// <summary>
    /// Fetches, validates, re-encodes and stores an image, returning a fully populated media
    /// payload. Returns null on any failure - a card without an image is a perfectly good card, so
    /// nothing here is worth failing an unfurl over.
    /// </summary>
    public async Task<EmbedMediaPayload?> ProcessAsync(string imageUrl, CancellationToken ct)
    {
        try
        {
            var fetched = await fetcher.FetchAsync(imageUrl, "image/*", Env.Unfurl.MaxImageBytes, ct);

            if (fetched.ContentType is null || !fetched.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;

            // Identify before decoding: this reads the header only, so an image claiming 40000x40000
            // is rejected without ever allocating a pixel buffer for it.
            var info = Image.Identify(fetched.Body);
            var megapixels = (long)info.Width * info.Height / 1_000_000d;
            if (megapixels > Env.Unfurl.MaxImageMegapixels)
            {
                logger.LogDebug("Rejected preview image {Url}: {Megapixels:F1}MP exceeds the ceiling", imageUrl, megapixels);
                return null;
            }

            using var image = Image.Load<Rgba32>(fetched.Body);

            var placeholder = ThumbHash.Encode(image);

            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(Env.Unfurl.MaxImageEdge, Env.Unfurl.MaxImageEdge),
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.Lanczos3,
            }));

            using var encoded = new MemoryStream();
            await image.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = 85 }, ct);
            encoded.Position = 0;

            // Content-addressed by source URL, so the same image posted in twenty channels is
            // stored once and the key is derivable without a lookup table.
            var key = $"{Env.Unfurl.MediaPrefix}/{HashUrl(imageUrl)}.jpg";

            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Env.StorageConfiguration.BucketName,
                Key = key,
                ContentType = "image/jpeg",
                InputStream = encoded,
            }, ct);

            return new EmbedMediaPayload
            {
                Url = imageUrl,
                ProxyUrl = $"{Env.Unfurl.PublicBaseUrl.TrimEnd('/')}/api/v1/previews/media/{HashUrl(imageUrl)}",
                Width = image.Width,
                Height = image.Height,
                ContentType = "image/jpeg",
                Placeholder = placeholder,
                PlaceholderVersion = 1,
            };
        }
        catch (FetchException e)
        {
            logger.LogDebug("Could not fetch preview image {Url}: {Reason}", imageUrl, e.Message);
            return null;
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            // The bytes were not an image, or were an image format ImageSharp will not decode.
            // Either way this is the origin's problem, not ours, and not worth a warning.
            logger.LogDebug("Preview image {Url} was not decodable", imageUrl);
            return null;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Unexpected failure processing preview image {Url}", imageUrl);
            return null;
        }
    }

    /// <summary>The S3 key and proxy path for a source URL. Public so the media controller can
    /// resolve a proxy request back to a stored object without a database.</summary>
    public static string HashUrl(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)));
}
