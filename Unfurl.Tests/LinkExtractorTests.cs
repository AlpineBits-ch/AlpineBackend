using System.Text;
using AppEnvironment;
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

    [Test]
    public void Extract_DotlessHostname_IsNotTreatedAsALink()
    {
        // Markdown autolink detection requires a dot in the host, so "http://localhost:8080/x" and
        // "http://intranet/page" are never recognised as links and never unfurled.
        Assert.Multiple(() =>
        {
            Assert.That(LinkExtractor.Extract("http://localhost:8080/health"), Is.Empty);
            Assert.That(LinkExtractor.Extract("http://intranet/page"), Is.Empty);
            Assert.That(LinkExtractor.Extract("http://127.0.0.1:8080/health"), Is.Not.Empty,
                "an IP literal still is a link - it is the dot that matters, not the routability");
        });
    }
}

/// <summary>
/// The second gate: of the links extracted above, which ones belong to this instance and must
/// therefore be answered in-process rather than fetched.
/// </summary>
public class InternalLinkRecognizerTests
{
    private static readonly string[] Hosts = ["app.venta.gg", "api.venta.gg"];

    private static InternalLink Recognize(string url)
    {
        Assert.That(InternalLinkRecognizer.TryRecognize(url, Hosts, out var link), Is.True,
            $"expected '{url}' to be recognized");
        return link!;
    }

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public void AnInviteLink_IsRecognizedWithItsCode()
    {
        var link = Recognize("https://app.venta.gg/invite/ABC23456");

        Assert.Multiple(() =>
        {
            Assert.That(link.Kind, Is.EqualTo(InternalLinkKind.Invite));
            Assert.That(link.Value("code"), Is.EqualTo("ABC23456"));
        });
    }

    [Test]
    public void AWikiLink_IsRecognizedWithBothIds()
    {
        var link = Recognize("https://app.venta.gg/wiki/gild_3H66JNBG/wkpg_7QZ1MMKT");

        Assert.Multiple(() =>
        {
            Assert.That(link.Kind, Is.EqualTo(InternalLinkKind.WikiPage));
            Assert.That(link.Value("guildId"), Is.EqualTo("gild_3H66JNBG"));
            Assert.That(link.Value("pageId"), Is.EqualTo("wkpg_7QZ1MMKT"));
        });
    }

    [Test]
    public void TheInviteSegment_MatchesTheDeepLinkPathTheClientsAlreadyParse()
    {
        // Two copies of "/invite" exist: this recogniser's route table and
        // WebClientHost.InvitePath, which the Steam and bot-install redirects are built from.
        Assert.That(AppEnvironment.WebClientHost.InvitePath, Is.EqualTo("/invite"));
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    [Test]
    public void AQueryStringAndTrailingSlash_DoNotStopTheMatch()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Recognize("https://app.venta.gg/invite/ABC23456/").Value("code"), Is.EqualTo("ABC23456"));
            Assert.That(Recognize("https://app.venta.gg/invite/ABC23456?utm_source=x").Value("code"),
                Is.EqualTo("ABC23456"));
        });
    }

    [Test]
    public void TheApiHostCountsToo()
    {
        // People paste whichever host their address bar showed. Both are this instance.
        Assert.That(Recognize("https://api.venta.gg/invite/ABC23456").Kind, Is.EqualTo(InternalLinkKind.Invite));
    }

    [Test]
    public void TheLiteralSegmentIsCaseInsensitive()
    {
        Assert.That(Recognize("https://app.venta.gg/Invite/ABC23456").Kind, Is.EqualTo(InternalLinkKind.Invite));
    }

    [Test]
    public void AnInstanceUrlWithNoKnownShape_IsInternalButNotRecognized()
    {
        // The distinction the unfurler depends on: still ours, so still never fetched, but there is
        // no card for it. Anything else would point the outbound fetcher at our own API.
        const string url = "https://app.venta.gg/settings/appearance";

        Assert.Multiple(() =>
        {
            Assert.That(InternalLinkRecognizer.IsInternal(url, Hosts), Is.True);
            Assert.That(InternalLinkRecognizer.TryRecognize(url, Hosts, out _), Is.False);
        });
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    [Test]
    public void AThirdPartyInviteUrl_IsNeitherInternalNorRecognized()
    {
        // Discord's own invite links have exactly this path.
        const string url = "https://discord.com/invite/ABC23456";

        Assert.Multiple(() =>
        {
            Assert.That(InternalLinkRecognizer.IsInternal(url, Hosts), Is.False);
            Assert.That(InternalLinkRecognizer.TryRecognize(url, Hosts, out _), Is.False);
        });
    }

    [Test]
    public void ALookalikeHost_IsNotThisInstance()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InternalLinkRecognizer.IsInternal("https://app.venta.gg.evil.test/invite/A1", Hosts), Is.False);
            Assert.That(InternalLinkRecognizer.IsInternal("https://notapp.venta.gg/invite/A1", Hosts), Is.False);
        });
    }

    [TestCase("https://app.venta.gg/invite", Description = "no code at all")]
    [TestCase("https://app.venta.gg/invite/", Description = "empty code")]
    [TestCase("https://app.venta.gg/invite/a/b", Description = "too many segments")]
    [TestCase("https://app.venta.gg/wiki/gild_1", Description = "missing the page")]
    [TestCase("https://app.venta.gg/wiki/gild_1/wkpg_1/history", Description = "past the page")]
    public void AMalformedInstanceUrl_IsNotRecognized(string url)
    {
        Assert.That(InternalLinkRecognizer.TryRecognize(url, Hosts, out _), Is.False);
    }

    [TestCase("https://app.venta.gg/invite/%2Fetc%2Fpasswd")]
    [TestCase("https://app.venta.gg/invite/..")]
    [TestCase("https://app.venta.gg/wiki/gild_1/%3Cscript%3E")]
    public void ARouteValueOutsideTheIdAlphabet_IsRejected(string url)
    {
        // Checked after percent-decoding, so a segment that decodes to a path separator, a quote or
        // a traversal never reaches a bus request or a rendered card.
        Assert.That(InternalLinkRecognizer.TryRecognize(url, Hosts, out _), Is.False);
    }

    [Test]
    public void AnAbsurdlyLongRouteValue_IsRejected()
    {
        var url = "https://app.venta.gg/invite/" + new string('A', 200);

        Assert.That(InternalLinkRecognizer.TryRecognize(url, Hosts, out _), Is.False);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not a url")]
    [TestCase("/invite/ABC23456")]
    public void Garbage_IsNotRecognized(string? url)
    {
        Assert.Multiple(() =>
        {
            Assert.That(InternalLinkRecognizer.IsInternal(url, Hosts), Is.False);
            Assert.That(InternalLinkRecognizer.TryRecognize(url, Hosts, out _), Is.False);
        });
    }
}

/// <summary>Which hostnames an instance answers to.</summary>
public class InstanceLinkHostsTests
{
    private string _instanceUrl = "";
    private string? _appDomain;
    private string? _extraHosts;

    [SetUp]
    public void Capture()
    {
        _instanceUrl = Env.GeneralConfiguration.InstanceUrl;
        _appDomain = Environment.GetEnvironmentVariable(WebClientHost.EnvironmentVariable);
        _extraHosts = Environment.GetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable);
    }

    [TearDown]
    public void Restore()
    {
        Env.GeneralConfiguration.InstanceUrl = _instanceUrl;
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, _appDomain);
        Environment.SetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable, _extraHosts);
    }

    [Test]
    public void BothTheApiHostAndTheDerivedWebClientHost_AreIncluded()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);

        Assert.That(InstanceLinkHosts.All, Is.EquivalentTo(new[] { "api.venta.gg", "app.venta.gg" }));
    }

    [Test]
    public void ATrailingSlashOnInstanceUrl_IsAbsorbed()
    {
        // Roughly half of operators write INSTANCE_URL with one.
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg/";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);

        Assert.That(InstanceLinkHosts.All, Contains.Item("api.venta.gg"));
    }

    [Test]
    public void ASelfHostedInstance_RecognizesItsOwnHostsAndNotOurs()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://api.chat.example.org";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);

        Assert.Multiple(() =>
        {
            Assert.That(InstanceLinkHosts.All, Is.EquivalentTo(new[] { "api.chat.example.org", "app.chat.example.org" }));
            Assert.That(InstanceLinkHosts.IsInstanceHost(new Uri("https://app.venta.gg/invite/A1")), Is.False);
        });
    }

    [Test]
    public void ExtraHosts_AcceptBareNamesAndUrlsInOneVariable()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable,
            "vanity.example, https://old.venta.gg/");

        Assert.That(InstanceLinkHosts.All, Is.SupersetOf(new[] { "vanity.example", "old.venta.gg" }));
    }

    [Test]
    public void ADefaultPortDeployment_MatchesItsHostOnAnyPort()
    {
        // Production is api.venta.gg on 443, and a link naming another port on that host is still
        // us - so the port must not narrow the match here.
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);

        Assert.That(InstanceLinkHosts.IsInstanceHost(new Uri("https://api.venta.gg:8443/invite/A1")), Is.True);
    }

    [Test]
    public void ADeploymentThatNamedAPort_MatchesOnlyThatPort()
    {
        // The developer and E2E case, and the reason the rule is not simply host-only.
        Env.GeneralConfiguration.InstanceUrl = "http://127.0.0.1:5001";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);

        Assert.Multiple(() =>
        {
            Assert.That(InstanceLinkHosts.IsInstanceHost(new Uri("http://127.0.0.1:5001/invite/A1")), Is.True);
            Assert.That(InstanceLinkHosts.IsInstanceHost(new Uri("http://127.0.0.1:5002/article")), Is.False);
        });
    }

    [Test]
    public void AnUnrelatedHost_IsNotThisInstance()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable(InstanceLinkHosts.EnvironmentVariable, null);

        Assert.Multiple(() =>
        {
            Assert.That(InstanceLinkHosts.IsInstanceHost(new Uri("https://example.com/invite/A1")), Is.False);
            Assert.That(InstanceLinkHosts.IsInstanceHost(null), Is.False);
        });
    }
}
