using Echo.Wiki;

namespace Echo.Tests.Wiki;

/// <summary>
/// The public wiki renders prose written by anyone holding CreateWikiPages in any guild on the
/// instance, on a venta.gg origin. These are the cases that decide whether that is safe.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WikiMarkdownTests
{
    private static readonly WikiLinkPolicy Policy =
        new(["media.venta.gg"], "https://api.venta.gg");

    /// <summary>One published sibling to link to, as the page renderer hands it over.</summary>
    private static readonly WikiLinkPolicy PolicyWithSibling = Policy with
    {
        PublishedPages = new Dictionary<string, string>
        {
            ["wkpg_published"] = "https://keep.wiki.venta.gg/the-keep-published",
        },
    };

    // ── Raw HTML never becomes markup ────────────────────────────────────────

    /// <summary>
    /// The case the whole design exists for. The assertion is about tags rather than about
    /// substrings like <c>onerror</c>, which survive as inert text and should.
    /// </summary>
    [TestCase("<script>alert(1)</script>", "<script")]
    [TestCase("<SCRIPT SRC=//evil.example/x.js></SCRIPT>", "<script")]
    [TestCase("<img src=x onerror=alert(1)>", "<img")]
    [TestCase("<iframe src=\"https://evil.example\"></iframe>", "<iframe")]
    [TestCase("<svg onload=alert(1)>", "<svg")]
    [TestCase("<a href=\"javascript:alert(1)\">click</a>", "<a ")]
    public void Raw_html_is_escaped_rather_than_rendered(string markdown, string forbidden)
    {
        var html = WikiMarkdown.Render(markdown, Policy);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain(forbidden).IgnoreCase);
            Assert.That(html, Does.Contain("&lt;"));
        });
    }

    /// <summary>An HTML block at the start of a line is a block parser's job, and there is no
    /// block parser for it.</summary>
    [Test]
    public void An_html_block_is_escaped_too()
    {
        var html = WikiMarkdown.Render("<div onclick=\"steal()\">\n\nhello\n", Policy);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("<div"));
            Assert.That(html, Does.Contain("&lt;div"));
        });
    }

    // ── Link destinations ────────────────────────────────────────────────────

    /// <summary>The other half of the attack surface: the one attacker-controlled value that does
    /// legitimately reach an attribute.</summary>
    [TestCase("[x](javascript:alert(1))")]
    [TestCase("[x](JavaScript:alert%281%29)")]
    [TestCase("[x](data:text/html;base64,PHNjcmlwdD4=)")]
    [TestCase("[x](vbscript:msgbox)")]
    [TestCase("[x](//evil.example/phish)")]
    public void A_dangerous_link_destination_is_neutralised(string markdown)
    {
        var html = WikiMarkdown.Render(markdown, Policy);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("href=\"#\""));
            Assert.That(html, Does.Not.Contain("javascript").IgnoreCase);
            Assert.That(html, Does.Not.Contain("vbscript").IgnoreCase);
            Assert.That(html, Does.Not.Contain("evil.example"));
            // The author's words survive; only where they pointed does not.
            Assert.That(html, Does.Contain(">x</a>"));
        });
    }

    /// <summary>
    /// A browser ignores a tab inside the scheme. The tab is written as an entity because that is
    /// the only way one reaches a link destination - a raw tab there stops markdown seeing a link at
    /// all - and it is resolved before anything looks at the scheme.
    /// </summary>
    [Test]
    public void A_scheme_broken_up_by_control_characters_is_still_recognised()
    {
        var html = WikiMarkdown.Render("[x](java&#9;script:alert&#40;1&#41;)", Policy);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("href=\"#\""));
            Assert.That(html, Does.Not.Contain("javascript:").IgnoreCase);
        });
    }

    [Test]
    public void An_external_link_carries_nofollow_ugc_and_no_window_handle()
    {
        var html = WikiMarkdown.Render("[x](https://example.com/a)", Policy);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("https://example.com/a"));
            Assert.That(html, Does.Contain("nofollow"));
            Assert.That(html, Does.Contain("ugc"));
            Assert.That(html, Does.Contain("noopener"));
        });
    }

    /// <summary>A sibling page is linked relatively, which is how a wiki cross-links.</summary>
    [Test]
    public void A_relative_link_is_left_alone()
    {
        var html = WikiMarkdown.Render("[x](other-page)", Policy);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("href=\"other-page\""));
            Assert.That(html, Does.Not.Contain("nofollow"));
        });
    }

    /// <summary>A leading slash resolves against the wiki host, which serves no application
    /// routes, so it has to be rebased onto the instance.</summary>
    [Test]
    public void A_rooted_link_is_rebased_onto_the_instance()
    {
        var html = WikiMarkdown.Render("[x](/channels/1)", Policy);

        Assert.That(html, Does.Contain("href=\"https://api.venta.gg/channels/1\""));
    }

    // ── Internal links ───────────────────────────────────────────────────────

    /// <summary>Every internal link on a published wiki used to render as a dead anchor.</summary>
    [Test]
    public void A_link_to_a_published_page_resolves_to_its_public_address()
    {
        var html = WikiMarkdown.Render("[the keep](wiki:wkpg_published)", PolicyWithSibling);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("href=\"https://keep.wiki.venta.gg/the-keep-published\""));
            // Same site, so it stays in this tab and carries no outbound rel.
            Assert.That(html, Does.Not.Contain("nofollow"));
            Assert.That(html, Does.Not.Contain("_blank"));
        });
    }

    [Test]
    public void A_link_to_a_heading_on_a_published_page_keeps_the_fragment()
    {
        var html = WikiMarkdown.Render("[the siege](wiki:wkpg_published#the-siege)", PolicyWithSibling);

        Assert.That(html, Does.Contain("href=\"https://keep.wiki.venta.gg/the-keep-published#the-siege\""));
    }

    /// <summary>A page the guild kept to itself must not get an address on the public site.</summary>
    [Test]
    public void A_link_to_a_page_that_is_not_published_is_neutralised()
    {
        var html = WikiMarkdown.Render("[the vault](wiki:wkpg_private)", PolicyWithSibling);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("href=\"#\""));
            Assert.That(html, Does.Not.Contain("wkpg_private"));
            Assert.That(html, Does.Contain(">the vault</a>"));
        });
    }

    /// <summary>Nothing resolves without a page map, which is every render but a page body.</summary>
    [Test]
    public void A_wiki_link_is_neutralised_when_no_pages_are_published()
    {
        var html = WikiMarkdown.Render("[the keep](wiki:wkpg_published)", Policy);

        Assert.That(html, Does.Contain("href=\"#\""));
    }

    // ── Images are a beacon, not just a picture ──────────────────────────────

    /// <summary>Every anonymous view would hand the chosen address a visitor's IP and user agent.</summary>
    [Test]
    public void An_image_on_an_unapproved_host_is_dropped()
    {
        var html = WikiMarkdown.Render("![](https://tracker.example/pixel.gif)", Policy);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("tracker.example"));
            Assert.That(html, Does.Not.Contain("<img"));
        });
    }

    [Test]
    public void An_image_on_the_instance_media_host_is_kept()
    {
        var html = WikiMarkdown.Render("![](https://media.venta.gg/a.png)", Policy);

        Assert.That(html, Does.Contain("https://media.venta.gg/a.png"));
    }

    [Test]
    public void A_rooted_image_is_rebased_onto_the_instance()
    {
        var html = WikiMarkdown.Render("![](/attachments/a.png)", Policy);

        Assert.That(html, Does.Contain("src=\"https://api.venta.gg/attachments/a.png\""));
    }

    [Test]
    public void A_protocol_relative_image_is_dropped()
    {
        var html = WikiMarkdown.Render("![](//media.venta.gg/a.png)", Policy);

        Assert.That(html, Does.Not.Contain("<img"));
    }

    // ── Ordinary markdown still works ────────────────────────────────────────

    [Test]
    public void Ordinary_markdown_still_renders()
    {
        var html = WikiMarkdown.Render("# Title\n\nSome **bold** text.\n", Policy);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<h1"));
            Assert.That(html, Does.Contain("<strong>bold</strong>"));
        });
    }

    [Test]
    public void An_empty_body_renders_to_nothing()
    {
        Assert.That(WikiMarkdown.Render("   ", Policy), Is.Empty);
    }

    // ── Summaries ────────────────────────────────────────────────────────────

    [Test]
    public void A_summary_carries_no_markup()
    {
        var summary = WikiMarkdown.Summarise("# Title\n\nSome **bold** text with a [link](https://x.example).", 200);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Not.Contain("**"));
            Assert.That(summary, Does.Not.Contain("#"));
            Assert.That(summary, Does.Not.Contain("https://x.example"));
            Assert.That(summary, Does.Contain("bold"));
        });
    }

    [Test]
    public void A_long_summary_is_cut()
    {
        var summary = WikiMarkdown.Summarise(string.Join(' ', Enumerable.Repeat("word", 200)), 60);

        Assert.That(summary, Has.Length.LessThanOrEqualTo(63));
        Assert.That(summary, Does.EndWith("..."));
    }
}
