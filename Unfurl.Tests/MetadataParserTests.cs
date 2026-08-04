using System.Text;
using Unfurl.Application.Parsing;

namespace Unfurl.Tests;

/// <summary>
/// Extraction precedence, and the sanitising that has to happen because every string here was
/// written by whoever controls the page.
/// </summary>
public class MetadataParserTests
{
    private readonly MetadataParser _parser = new();
    private static readonly Uri BaseUrl = new("https://example.com/article");

    private Task<PageMetadata> ParseAsync(string html) =>
        _parser.ParseAsync(Encoding.UTF8.GetBytes(html), BaseUrl, CancellationToken.None);

    // ── Precedence ───────────────────────────────────────────────────────────

    [Test]
    public async Task Parse_OpenGraphPresent_WinsOverEverythingElse()
    {
        var meta = await ParseAsync("""
            <html><head>
              <title>HTML title</title>
              <meta name="description" content="HTML description">
              <meta name="twitter:title" content="Twitter title">
              <meta property="og:title" content="OG title">
              <meta property="og:description" content="OG description">
              <meta property="og:site_name" content="Example Site">
            </head><body></body></html>
            """);

        Assert.Multiple(() =>
        {
            Assert.That(meta.Title, Is.EqualTo("OG title"));
            Assert.That(meta.Description, Is.EqualTo("OG description"));
            Assert.That(meta.SiteName, Is.EqualTo("Example Site"));
        });
    }

    [Test]
    public async Task Parse_TwitterOnly_IsUsed()
    {
        var meta = await ParseAsync("""
            <html><head>
              <title>HTML title</title>
              <meta name="twitter:title" content="Twitter title">
              <meta name="twitter:description" content="Twitter description">
            </head><body></body></html>
            """);

        Assert.Multiple(() =>
        {
            Assert.That(meta.Title, Is.EqualTo("Twitter title"));
            Assert.That(meta.Description, Is.EqualTo("Twitter description"));
        });
    }

    [Test]
    public async Task Parse_BareHtmlOnly_FallsBackToTitleAndDescription()
    {
        var meta = await ParseAsync("""
            <html><head>
              <title>Just a title</title>
              <meta name="description" content="Just a description">
            </head><body></body></html>
            """);

        Assert.Multiple(() =>
        {
            Assert.That(meta.Title, Is.EqualTo("Just a title"));
            Assert.That(meta.Description, Is.EqualTo("Just a description"));
            Assert.That(meta.SiteName, Is.EqualTo("example.com"), "falls back to the host");
        });
    }

    [Test]
    public async Task Parse_NothingAtAll_YieldsNulls()
    {
        var meta = await ParseAsync("<html><body><p>no metadata here</p></body></html>");

        Assert.Multiple(() =>
        {
            Assert.That(meta.Title, Is.Null);
            Assert.That(meta.Description, Is.Null);
            Assert.That(meta.ImageUrl, Is.Null);
        });
    }

    // ── Images ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Parse_RelativeImage_IsResolvedAgainstThePage()
    {
        var meta = await ParseAsync("""
            <html><head><meta property="og:image" content="/img/hero.png"></head></html>
            """);

        Assert.That(meta.ImageUrl, Is.EqualTo("https://example.com/img/hero.png"));
    }

    [Test]
    public async Task Parse_SecureImageUrl_IsPreferred()
    {
        var meta = await ParseAsync("""
            <html><head>
              <meta property="og:image" content="http://example.com/insecure.png">
              <meta property="og:image:secure_url" content="https://example.com/secure.png">
            </head></html>
            """);

        Assert.That(meta.ImageUrl, Is.EqualTo("https://example.com/secure.png"));
    }

    [Test]
    public async Task Parse_NonHttpImage_IsDropped()
    {
        var meta = await ParseAsync("""
            <html><head><meta property="og:image" content="javascript:alert(1)"></head></html>
            """);

        Assert.That(meta.ImageUrl, Is.Null);
    }

    [Test]
    public async Task Parse_SummaryLargeImage_AsksForTheBigLayout()
    {
        var meta = await ParseAsync("""
            <html><head>
              <meta name="twitter:card" content="summary_large_image">
              <meta property="og:image" content="https://example.com/hero.png">
            </head></html>
            """);

        Assert.That(meta.PrefersLargeImage, Is.True);
    }

    // ── Canonical URL ────────────────────────────────────────────────────────

    [Test]
    public async Task Parse_OgUrlOnAnotherHost_IsIgnored()
    {
        // A page can claim any canonical URL it likes.
        var meta = await ParseAsync("""
            <html><head><meta property="og:url" content="https://yourbank.example/login"></head></html>
            """);

        Assert.That(meta.CanonicalUrl, Is.Null);
    }

    [Test]
    public async Task Parse_OgUrlOnTheSameHost_IsKept()
    {
        var meta = await ParseAsync("""
            <html><head><meta property="og:url" content="https://example.com/canonical"></head></html>
            """);

        Assert.That(meta.CanonicalUrl, Is.EqualTo("https://example.com/canonical"));
    }

    // ── Sanitising ───────────────────────────────────────────────────────────

    [Test]
    public async Task Parse_HtmlEntities_AreDecoded()
    {
        var meta = await ParseAsync("""
            <html><head><meta property="og:title" content="Tom &amp; Jerry &lt;3"></head></html>
            """);

        Assert.That(meta.Title, Is.EqualTo("Tom & Jerry <3"));
    }

    [Test]
    public async Task Parse_NewlinesAndTabs_AreCollapsed()
    {
        var meta = await ParseAsync("<html><head><meta property=\"og:title\" content=\"one\ntwo\t\tthree\"></head></html>");

        Assert.That(meta.Title, Is.EqualTo("one two three"));
    }

    [Test]
    public async Task Parse_BidiOverrideCharacters_AreStripped()
    {
        // U+202E reverses everything after it in most renderers - the trick that makes "gpj.exe"
        // read as "exe.jpg". It can never be legitimate in a page title.
        var meta = await ParseAsync("<html><head><meta property=\"og:title\" content=\"safe‮dangerous\"></head></html>");

        Assert.Multiple(() =>
        {
            Assert.That(meta.Title, Does.Not.Contain('‮'));
            Assert.That(meta.Title, Is.EqualTo("safedangerous"));
        });
    }

    [Test]
    public async Task Parse_ScriptTagInTitle_IsInertText()
    {
        // AngleSharp decodes it as text; it must never come back out as markup.
        var meta = await ParseAsync("""
            <html><head><meta property="og:title" content="&lt;script&gt;alert(1)&lt;/script&gt;"></head></html>
            """);

        Assert.That(meta.Title, Is.EqualTo("<script>alert(1)</script>"),
            "stored as literal text - escaping on render is the client's job, but nothing here may execute");
    }

    [Test]
    public async Task Parse_WhitespaceOnlyContent_BecomesNull()
    {
        var meta = await ParseAsync("""
            <html><head><meta property="og:title" content="   "></head></html>
            """);

        Assert.That(meta.Title, Is.Null);
    }

    [Test]
    public async Task Parse_MalformedHtml_StillYieldsWhatItCan()
    {
        // Real-world markup: unclosed tags, unquoted attributes, junk.
        var meta = await ParseAsync("""
            <html><head<meta property="og:title" content="Survived">
            <meta property=og:description content=Unquoted>
            <body><p>oops
            """);

        Assert.That(meta, Is.Not.Null);
    }

    [Test]
    public async Task Parse_EmptyDocument_DoesNotThrow()
    {
        var meta = await ParseAsync("");

        Assert.That(meta.Title, Is.Null);
    }
}
