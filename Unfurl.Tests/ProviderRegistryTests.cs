using System.Reflection;
using Bots.Contracts.Gateway.Payloads;
using Unfurl.Application.Parsing;
using Unfurl.Application.Providers;

namespace Unfurl.Tests;

/// <summary>The provider registry and the whitelist rule it exists to enforce.</summary>
public class ProviderRegistryTests
{
    // ── YouTube ──────────────────────────────────────────────────────────────

    [TestCase("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [TestCase("https://youtube.com/watch?v=dQw4w9WgXcQ&t=42")]
    [TestCase("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [TestCase("https://youtu.be/dQw4w9WgXcQ")]
    [TestCase("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [TestCase("https://www.youtube.com/live/dQw4w9WgXcQ")]
    [TestCase("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [TestCase("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    public void Match_YouTubeUrlForms_AllResolveToTheSamePlayer(string url)
    {
        var match = ProviderRegistry.Match(new Uri(url));

        Assert.That(match, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(match!.ProviderName, Is.EqualTo("YouTube"));
            Assert.That(match.EmbedType, Is.EqualTo(EmbedTypes.Video));
            Assert.That(match.PlayerUrl, Is.EqualTo("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ"));
            Assert.That(match.ThumbnailUrl, Does.Contain("dQw4w9WgXcQ"));
        });
    }

    /// <summary>The player must stay on the no-cookie host.</summary>
    [Test]
    public void Match_YouTube_PlayerIsOnTheNoCookieHost()
    {
        var match = ProviderRegistry.Match(new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));

        Assert.Multiple(() =>
        {
            // Exactly "www.youtube-nocookie.com".
            Assert.That(new Uri(match!.PlayerUrl).Host, Is.EqualTo("www.youtube-nocookie.com"));

            // The card's "open on YouTube" affordance still points at the real site - only the
            // embedded frame moves.
            Assert.That(match.ProviderUrl, Is.EqualTo("https://www.youtube.com"));
        });
    }

    [Test]
    public void Match_YouTubeWithoutVideoId_DoesNotMatch()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProviderRegistry.Match(new Uri("https://www.youtube.com/")), Is.Null);
            Assert.That(ProviderRegistry.Match(new Uri("https://www.youtube.com/watch")), Is.Null);
            Assert.That(ProviderRegistry.Match(new Uri("https://www.youtube.com/results?search_query=x")), Is.Null);
        });
    }

    [TestCase("https://www.youtube.com/watch?v=../../evil")]
    [TestCase("https://www.youtube.com/watch?v=abc%2F%3Fx%3D1")]
    [TestCase("https://www.youtube.com/watch?v=a")]
    public void Match_MalformedVideoId_IsRejectedRatherThanInterpolated(string url)
    {
        // The id is pasted into a URL we build, so anything that could carry a "/" or "?" out of
        // its slot has to be refused rather than escaped - escaping is the thing people get wrong.
        Assert.That(ProviderRegistry.Match(new Uri(url)), Is.Null);
    }

    // ── Other providers ──────────────────────────────────────────────────────

    [Test]
    public void Match_Vimeo_BuildsThePlayerUrl()
    {
        var match = ProviderRegistry.Match(new Uri("https://vimeo.com/123456789"));

        Assert.That(match, Is.Not.Null);
        Assert.That(match!.PlayerUrl, Is.EqualTo("https://player.vimeo.com/video/123456789"));
    }

    [Test]
    public void Match_TwitchChannel_PinsParentToOurOwnHost()
    {
        // Twitch refuses to load a player unless the embedding host is declared, and the value has
        // to be OUR domain - never anything taken from the link.
        var match = ProviderRegistry.Match(new Uri("https://www.twitch.tv/somestreamer"));

        Assert.That(match, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(match!.PlayerUrl, Does.Contain("channel=somestreamer"));
            Assert.That(match.PlayerUrl, Does.Contain("parent="));
            Assert.That(match.PlayerUrl, Does.Not.Contain("parent=twitch.tv"));
        });
    }

    [Test]
    public void Match_TwitchVod_UsesTheVideoPlayer()
    {
        var match = ProviderRegistry.Match(new Uri("https://www.twitch.tv/videos/1234567890"));

        Assert.That(match!.PlayerUrl, Does.Contain("video=1234567890"));
    }

    [TestCase("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", "track")]
    [TestCase("https://open.spotify.com/album/4cOdK2wGLETKBW3PvgPWqT", "album")]
    [TestCase("https://open.spotify.com/playlist/4cOdK2wGLETKBW3PvgPWqT", "playlist")]
    public void Match_Spotify_BuildsTheEmbedForEachKind(string url, string kind)
    {
        var match = ProviderRegistry.Match(new Uri(url));

        Assert.That(match, Is.Not.Null);
        Assert.That(match!.PlayerUrl, Does.Contain($"/embed/{kind}/"));
    }

    [Test]
    public void Match_SpotifyUnknownKind_DoesNotMatch()
    {
        Assert.That(ProviderRegistry.Match(new Uri("https://open.spotify.com/user/4cOdK2wGLETKBW3PvgPWqT")), Is.Null);
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    [TestCase("https://example.com/watch?v=dQw4w9WgXcQ")]
    [TestCase("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [TestCase("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    public void Match_LookalikeHosts_DoNotMatch(string url)
    {
        // youtube.com.evil.example is the important one: a suffix check instead of an exact host
        // match would hand an attacker's page a video player slot.
        Assert.That(ProviderRegistry.Match(new Uri(url)), Is.Null);
    }

    [Test]
    public void Match_OrdinaryArticle_DoesNotMatch()
    {
        Assert.That(ProviderRegistry.Match(new Uri("https://example.com/blog/post")), Is.Null);
    }

    // ── The architecture rule ────────────────────────────────────────────────

    /// <summary>A client renders <c>video.url</c> in an iframe.</summary>
    [Test]
    public void PageMetadata_CannotCarryAPlayerUrl()
    {
        var suspicious = typeof(PageMetadata)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Video", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Player", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Iframe", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Embed", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Html", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();

        Assert.That(suspicious, Is.Empty,
            "PageMetadata is populated from attacker-controlled HTML and must never be able to " +
            "express an embeddable player. Only ProviderRegistry may produce one, from its whitelist.");
    }

    [Test]
    public void EveryProviderPlayerUrl_IsHttpsAndOnTheProvidersOwnHost()
    {
        var samples = new[]
        {
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "https://vimeo.com/123456789",
            "https://www.twitch.tv/somestreamer",
            "https://www.twitch.tv/videos/1234567890",
            "https://clips.twitch.tv/SomeClipSlug",
            "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT",
        };

        var allowedHosts = new[]
        {
            "www.youtube-nocookie.com", "player.vimeo.com", "player.twitch.tv", "clips.twitch.tv",
            "open.spotify.com",
        };

        foreach (var sample in samples)
        {
            var match = ProviderRegistry.Match(new Uri(sample));
            Assert.That(match, Is.Not.Null, sample);

            var player = new Uri(match!.PlayerUrl);
            Assert.Multiple(() =>
            {
                Assert.That(player.Scheme, Is.EqualTo("https"), sample);
                Assert.That(allowedHosts, Does.Contain(player.Host), sample);
            });
        }
    }
}
