using AppEnvironment;
using AppEnvironment.Security;
using Bots.Contracts.Gateway.Payloads;
using Unfurl.Application.Caching;
using Unfurl.Application.Fetching;
using Unfurl.Application.Media;
using Unfurl.Application.Parsing;
using Unfurl.Application.Providers;
using Unfurl.Contracts.Bus;

namespace Unfurl.Application.Services;

/// <summary>
/// URL in, embed out. The whole of what this service does.
///
/// <para>It knows nothing about messages, channels, users or permissions - by design. Messaging
/// decides <i>which</i> links to resolve and who may suppress the result; this decides only what a
/// given URL looks like. Keeping the split that clean is what makes it safe for the risky half -
/// arbitrary outbound HTTP against attacker-chosen hosts - to run in its own process, with its own
/// egress policy, away from the message store and the MLS key material.</para>
/// </summary>
public class UnfurlService(
    SafeFetcher fetcher,
    MetadataParser parser,
    MediaProcessor media,
    UnfurlCache cache,
    ILogger<UnfurlService> logger)
{
    public async Task<UnfurlResult> ResolveAsync(string url, CancellationToken ct)
    {
        if (!Env.Unfurl.Enabled)
            return Failed(url, "Unfurling is disabled on this instance.");

        // Re-validated even though the caller filtered: this service is reachable over the bus and
        // must not depend on a peer having done its job.
        if (!OutboundAddressGuard.TryValidate(url, Env.Unfurl.AllowPrivateTargets, out var uri, out var error))
            return Failed(url, error ?? "Invalid URL.");

        var cached = await cache.GetAsync(url, ct);
        if (cached is not null)
        {
            return cached.Embed is not null
                ? new UnfurlResult { Url = url, Embed = cached.Embed }
                : Failed(url, cached.FailureReason ?? "Previously failed.");
        }

        try
        {
            var embed = await BuildAsync(uri!, url, ct);
            return new UnfurlResult { Url = url, Embed = embed };
        }
        catch (FetchException e)
        {
            await cache.SetFailureAsync(url, e.Message, ct);
            return Failed(url, e.Message);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Unexpected failure unfurling a URL");
            await cache.SetFailureAsync(url, "Unexpected failure.", ct);
            return Failed(url, "Unexpected failure.");
        }
    }

    private async Task<EmbedPayload> BuildAsync(Uri uri, string originalUrl, CancellationToken ct)
    {
        // A link straight to an image file is an image embed - no HTML to parse, and Discord does
        // the same. Cheap to detect from the extension and confirmed by the fetched content type.
        var provider = ProviderRegistry.Match(uri);

        var fetched = await fetcher.FetchAsync(
            originalUrl, "text/html,application/xhtml+xml,image/*;q=0.8", Env.Unfurl.MaxHtmlBytes, ct);

        if (fetched.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            var direct = await BuildDirectImageAsync(originalUrl, ct);
            await cache.SetAsync(originalUrl, direct, fetched.OriginTtl, ct);
            return direct;
        }

        if (fetched.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true
            && fetched.ContentType?.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new FetchException($"Unsupported content type '{fetched.ContentType}'.");
        }

        var metadata = await parser.ParseAsync(fetched.Body, fetched.FinalUrl, ct);

        var embed = new EmbedPayload
        {
            Type = ResolveType(metadata, provider),
            Title = metadata.Title,
            Description = metadata.Description,
            Url = metadata.CanonicalUrl ?? originalUrl,
            Timestamp = metadata.PublishedAt,
            Color = metadata.ThemeColor,
            Provider = new EmbedProviderPayload
            {
                Name = provider?.ProviderName ?? metadata.SiteName ?? fetched.FinalUrl.Host,
                Url = provider?.ProviderUrl ?? $"{fetched.FinalUrl.Scheme}://{fetched.FinalUrl.Host}",
            },
        };

        if (!string.IsNullOrWhiteSpace(metadata.AuthorName) || !string.IsNullOrWhiteSpace(metadata.AuthorUrl))
        {
            embed.Author = new EmbedAuthorPayload
            {
                Name = metadata.AuthorName ?? "",
                Url = metadata.AuthorUrl,
            };
        }

        // The provider's own poster frame wins over the page's og:image where it has one, because
        // it is derived from the video id and is therefore always the right frame for the right
        // video - og:image on a video page is frequently the site's logo.
        var imageUrl = provider?.ThumbnailUrl ?? metadata.ImageUrl;

        if (imageUrl is not null)
        {
            var processed = await media.ProcessAsync(imageUrl, ct);
            if (processed is not null)
            {
                // A player's still is a thumbnail (the client draws a play button over it); an
                // article's hero image is a full-width image. The distinction is what stops every
                // link turning into a giant picture.
                if (provider is not null || !metadata.PrefersLargeImage)
                    embed.Thumbnail = processed;
                else
                    embed.Image = processed;
            }
        }

        // The single place a player may be attached, and only from the registry's whitelist. See
        // ProviderRegistry's remarks: nothing derived from the fetched page can reach this field.
        if (provider is not null)
        {
            embed.Video = new EmbedMediaPayload
            {
                Url = provider.PlayerUrl,
                Width = provider.PlayerWidth,
                Height = provider.PlayerHeight,
            };
        }

        EmbedLimits.Clamp(embed);
        await cache.SetAsync(originalUrl, embed, fetched.OriginTtl, ct);
        return embed;
    }

    private async Task<EmbedPayload> BuildDirectImageAsync(string url, CancellationToken ct)
    {
        var processed = await media.ProcessAsync(url, ct);
        if (processed is null) throw new FetchException("Image could not be processed.");

        return new EmbedPayload
        {
            Type = EmbedTypes.Image,
            Url = url,
            Image = processed,
        };
    }

    private static string ResolveType(PageMetadata metadata, ProviderMatch? provider)
    {
        if (provider is not null) return provider.EmbedType;

        var ogType = metadata.OpenGraphType?.ToLowerInvariant();

        return ogType switch
        {
            // og:type video.* on a site we have no player for still gets a card, not a player -
            // the type only tells a client how to lay it out.
            not null when ogType.StartsWith("article") => EmbedTypes.Article,
            "image" => EmbedTypes.Image,
            _ => EmbedTypes.Link,
        };
    }

    private static UnfurlResult Failed(string url, string reason) =>
        new() { Url = url, FailureReason = reason };
}
