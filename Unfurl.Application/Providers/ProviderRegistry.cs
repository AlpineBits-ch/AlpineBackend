using System.Text.RegularExpressions;
using System.Web;
using Bots.Contracts.Gateway.Payloads;

namespace Unfurl.Application.Providers;

/// <summary>
/// What a provider match produced: the player iframe URL, its aspect, and the poster frame.
/// </summary>
public sealed record ProviderMatch(
    string ProviderName,
    string ProviderUrl,
    string EmbedType,
    string PlayerUrl,
    int PlayerWidth,
    int PlayerHeight,
    string? ThumbnailUrl);

/// <summary>
/// Recognises the handful of sites whose links should become an in-app player rather than a card,
/// and builds the player URL for them.
/// </summary>
public static class ProviderRegistry
{
    private const string YouTubeThumbnail = "https://i.ytimg.com/vi/{0}/maxresdefault.jpg";

    // Video ids are opaque tokens; pinning the character class stops a crafted id from breaking out
    // of the player URL we build with it (a "/" or "?" would append arbitrary path or query).
    private static readonly Regex YouTubeId = new(@"^[A-Za-z0-9_-]{6,20}$", RegexOptions.Compiled);
    private static readonly Regex NumericId = new(@"^[0-9]{5,15}$", RegexOptions.Compiled);
    private static readonly Regex SpotifyId = new(@"^[A-Za-z0-9]{16,32}$", RegexOptions.Compiled);
    private static readonly Regex TwitchSlug = new(@"^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled);

    /// <summary>Attempts to match a URL to a known player.</summary>
    public static ProviderMatch? Match(Uri url)
    {
        var host = Normalize(url.Host);

        return host switch
        {
            "youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtube-nocookie.com" => YouTube(url),
            "youtu.be" => YouTubeShort(url),
            "vimeo.com" or "player.vimeo.com" => Vimeo(url),
            "twitch.tv" => Twitch(url),
            "clips.twitch.tv" => TwitchClip(url),
            "open.spotify.com" => Spotify(url),
            _ => null,
        };
    }

    private static string Normalize(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..].ToLowerInvariant() : host.ToLowerInvariant();

    private static ProviderMatch? YouTube(Uri url)
    {
        var path = url.AbsolutePath.Trim('/');

        // /watch?v=ID, /shorts/ID, /live/ID and /embed/ID all resolve to the same player.
        var id = path.Equals("watch", StringComparison.OrdinalIgnoreCase)
            ? HttpUtility.ParseQueryString(url.Query)["v"]
            : path.Split('/') is ["shorts" or "live" or "embed" or "v", var tail, ..] ? tail : null;

        return BuildYouTube(id);
    }

    private static ProviderMatch? YouTubeShort(Uri url) =>
        BuildYouTube(url.AbsolutePath.Trim('/').Split('/').FirstOrDefault());

    private static ProviderMatch? BuildYouTube(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !YouTubeId.IsMatch(id)) return null;

        // youtube-nocookie.com, not youtube.com: it is the same player on the same infrastructure,
        // but it does not set YouTube's advertising and personalisation cookies unless the viewer
        // actually starts playback, and it does not attach what they watched here to their Google
        // profile for ad targeting.
        return new ProviderMatch(
            "YouTube", "https://www.youtube.com", EmbedTypes.Video,
            $"https://www.youtube-nocookie.com/embed/{id}", 1280, 720,
            string.Format(YouTubeThumbnail, id));
    }

    private static ProviderMatch? Vimeo(Uri url)
    {
        var id = url.AbsolutePath.Trim('/').Split('/').FirstOrDefault(segment => NumericId.IsMatch(segment));
        if (id is null) return null;

        // No thumbnail: Vimeo's poster frame is not derivable from the id, so it comes from the
        // page's own og:image on the generic pass that runs alongside this.
        return new ProviderMatch(
            "Vimeo", "https://vimeo.com", EmbedTypes.Video,
            $"https://player.vimeo.com/video/{id}", 1280, 720, null);
    }

    private static ProviderMatch? Twitch(Uri url)
    {
        var segments = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        // Twitch's player refuses to load unless the embedding page's host is declared up front,
        // which means the parent parameter has to be our own domain - not something derived from
        // the link.
        var parent = new Uri(AppEnvironment.Env.Unfurl.PublicBaseUrl).Host;

        // /videos/{id} is a VOD; /{channel} is a live channel; /{channel}/clip/{slug} is a clip.
        if (segments is ["videos", var vod, ..] && NumericId.IsMatch(vod))
            return TwitchMatch($"https://player.twitch.tv/?video={vod}&parent={parent}");

        if (segments is [var channel, "clip", var slug, ..] && TwitchSlug.IsMatch(slug) && TwitchSlug.IsMatch(channel))
            return TwitchMatch($"https://clips.twitch.tv/embed?clip={slug}&parent={parent}");

        if (segments is [var live] && TwitchSlug.IsMatch(live))
            return TwitchMatch($"https://player.twitch.tv/?channel={live}&parent={parent}");

        return null;
    }

    private static ProviderMatch? TwitchClip(Uri url)
    {
        var slug = url.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
        if (slug is null || !TwitchSlug.IsMatch(slug)) return null;

        var parent = new Uri(AppEnvironment.Env.Unfurl.PublicBaseUrl).Host;
        return TwitchMatch($"https://clips.twitch.tv/embed?clip={slug}&parent={parent}");
    }

    private static ProviderMatch TwitchMatch(string playerUrl) =>
        new("Twitch", "https://www.twitch.tv", EmbedTypes.Video, playerUrl, 1280, 720, null);

    private static ProviderMatch? Spotify(Uri url)
    {
        var segments = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        var kind = segments[0].ToLowerInvariant();
        var id = segments[1];

        if (kind is not ("track" or "album" or "playlist" or "artist" or "episode" or "show")) return null;
        if (!SpotifyId.IsMatch(id)) return null;

        // Spotify's embed is an audio player, so it gets a squat player box rather than 16:9.
        return new ProviderMatch(
            "Spotify", "https://open.spotify.com", EmbedTypes.Video,
            $"https://open.spotify.com/embed/{kind}/{id}", 640, kind is "track" or "episode" ? 152 : 380, null);
    }
}
