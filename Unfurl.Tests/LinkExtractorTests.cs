using System.Text;
using Bots.Contracts.Gateway.Payloads;
using Messaging.Domain.Previews;

namespace Unfurl.Tests;

/// <summary>What gets a preview and what does not.</summary>
public class LinkExtractorTests
{
    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public void Extract_BareUrl_IsFound()
    {
        var urls = LinkExtractor.Extract("look at https://example.com/article please");

        Assert.That(urls, Is.EqualTo(new[] { "https://example.com/article" }));
    }

    [Test]
    public void Extract_MarkdownLink_IsFound()
    {
        var urls = LinkExtractor.Extract("[the article](https://example.com/a)");

        Assert.That(urls, Is.EqualTo(new[] { "https://example.com/a" }));
    }

    [Test]
    public void Extract_SeveralUrls_KeepsFirstAppearanceOrder()
    {
        var urls = LinkExtractor.Extract("https://a.example https://b.example https://c.example");

        Assert.That(urls, Is.EqualTo(new[] { "https://a.example/", "https://b.example/", "https://c.example/" }));
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    [Test]
    public void Extract_SameUrlTwice_IsDeduped()
    {
        var urls = LinkExtractor.Extract("https://example.com/x and again https://example.com/x");

        Assert.That(urls, Has.Count.EqualTo(1));
    }

    [Test]
    public void Extract_UrlsDifferingOnlyByFragment_AreOneUrl()
    {
        // The fragment never reaches the server, so both would produce byte-identical responses.
        var urls = LinkExtractor.Extract("https://example.com/p#one https://example.com/p#two");

        Assert.That(urls, Is.EqualTo(new[] { "https://example.com/p" }));
    }

    [Test]
    public void Extract_MoreThanTheCap_StopsAtTheCap()
    {
        var content = string.Join(' ', Enumerable.Range(1, 12).Select(i => $"https://example{i}.com/"));

        var urls = LinkExtractor.Extract(content);

        Assert.That(urls, Has.Count.EqualTo(EmbedLimits.MaxUnfurledPerMessage));
    }

    [Test]
    public void Extract_InlineCodeSpan_IsSkipped()
    {
        // Someone pasting a URL in backticks is showing it as text, not linking to it.
        var urls = LinkExtractor.Extract("use `https://example.com/api` as the base");

        Assert.That(urls, Is.Empty);
    }

    [Test]
    public void Extract_FencedCodeBlock_IsSkipped()
    {
        var content = "here:\n```\ncurl https://example.com/api\n```\n";

        var urls = LinkExtractor.Extract(content);

        Assert.That(urls, Is.Empty);
    }

    [Test]
    public void Extract_AngleBracketed_IsSkipped()
    {
        // <https://…> is the sender saying "link it, do not unfurl it".
        var urls = LinkExtractor.Extract("no card please <https://example.com/x>");

        Assert.That(urls, Is.Empty);
    }

    [Test]
    public void Extract_AngleBracketedAmongPlainOnes_SkipsOnlyThatOne()
    {
        var urls = LinkExtractor.Extract("<https://quiet.example/> but https://loud.example/ is fine");

        Assert.That(urls, Is.EqualTo(new[] { "https://loud.example/" }));
    }

    [Test]
    public void Extract_FromUtf8Bytes_MatchesTheStringOverload()
    {
        var urls = LinkExtractor.Extract(Encoding.UTF8.GetBytes("https://example.com/b"));

        Assert.That(urls, Is.EqualTo(new[] { "https://example.com/b" }));
    }

    [Test]
    public void Extract_FromCiphertextBytes_YieldsNothing()
    {
        // An MLS-encrypted body is not valid UTF-8.
        var ciphertext = new byte[] { 0xC3, 0x28, 0xA0, 0xA1, 0xFF, 0xFE, 0x00, 0x9C };

        Assert.That(LinkExtractor.Extract(ciphertext), Is.Empty);
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    [Test]
    public void Extract_NoLinks_YieldsNothing()
    {
        Assert.That(LinkExtractor.Extract("just some words"), Is.Empty);
    }

    [Test]
    public void Extract_NullOrEmpty_YieldsNothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LinkExtractor.Extract((string?)null), Is.Empty);
            Assert.That(LinkExtractor.Extract(""), Is.Empty);
            Assert.That(LinkExtractor.Extract((byte[]?)null), Is.Empty);
        });
    }

    [TestCase("javascript:alert(1)")]
    [TestCase("data:text/html;base64,PHNjcmlwdD4=")]
    [TestCase("file:///etc/passwd")]
    [TestCase("ftp://example.com/x")]
    public void Extract_NonHttpSchemes_AreRejected(string url)
    {
        Assert.That(LinkExtractor.Extract($"look {url} here"), Is.Empty);
    }

    [Test]
    public void Extract_UrlWithEmbeddedCredentials_IsRejected()
    {
        // The fetcher would send those credentials to the origin, so a crafted message could make
        // the server hand a token to a host of the sender's choosing.
        var urls = LinkExtractor.Extract("https://user:secret@example.com/x");

        Assert.That(urls, Is.Empty);
    }

    [Test]
    public void Extract_RelativeUrl_IsRejected()
    {
        Assert.That(LinkExtractor.Extract("see /docs/page for details"), Is.Empty);
    }
}
