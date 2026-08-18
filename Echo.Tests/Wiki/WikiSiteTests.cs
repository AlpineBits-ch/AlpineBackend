using System.Text.RegularExpressions;
using Echo.Sites;
using Echo.Wiki;

namespace Echo.Tests.Wiki;

/// <summary>
/// The public wiki's hostname, its security headers and what a rendered page is allowed to say
/// about the people who wrote it.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WikiSiteTests
{
    private static readonly WikiRenderContext Context = new(
        Scheme: "https",
        ApexHost: "wiki.venta.gg",
        Links: new WikiLinkPolicy(["media.venta.gg"], "https://api.venta.gg"),
        SupportUrl: "https://support.venta.gg/contact",
        InstanceName: "venta");

    private static PublicWikiPage Page(string title = "A page", string content = "hello") => new(
        Slug: "a-page-12345678",
        Title: title,
        Content: content,
        Icon: null,
        CoverUrl: null,
        Category: null,
        UpdatedAt: new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
        GuildName: "A Guild",
        WikiSlug: "a-guild");

    // ── The host is a sibling of the API, like every other site ──────────────

    [TestCase("https://api.venta.gg", "wiki.venta.gg")]
    [TestCase("https://chat.example.com", "wiki.example.com")]
    [TestCase("https://venta.gg", "wiki.venta.gg")]
    [TestCase("https://example.com", "wiki.example.com")]
    [TestCase("http://localhost:8080", "wiki.localhost")]
    [TestCase("http://192.168.1.10:8080", "wiki.192.168.1.10")]
    [TestCase("https://wiki.venta.gg", "wiki.venta.gg")]
    public void The_wiki_host_is_derived_the_same_way_every_other_site_is(
        string instanceUrl, string expected)
    {
        Assert.That(SiteHost.DeriveFrom(WikiHosting.Label, instanceUrl), Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("not a url")]
    public void A_misconfigured_instance_url_falls_back_rather_than_throwing(string? instanceUrl)
    {
        Assert.That(SiteHost.DeriveFrom(WikiHosting.Label, instanceUrl), Is.EqualTo("wiki.localhost"));
    }

    [TestCase("https://api.venta.gg")]
    [TestCase("https://venta.gg")]
    [TestCase("http://localhost:8080")]
    public void The_wiki_never_lands_on_another_site_host(string instanceUrl)
    {
        var hosts = new[]
        {
            SiteHost.DeriveFrom(WikiHosting.Label, instanceUrl),
            SiteHost.DeriveFrom(SiteHosting.AdminLabel, instanceUrl),
            SiteHost.DeriveFrom(SiteHosting.SupportLabel, instanceUrl),
            SiteHost.DeriveFrom(SiteHosting.StatusLabel, instanceUrl),
            SiteHost.DeriveFrom(SiteHosting.AuthLabel, instanceUrl),
        };

        Assert.That(hosts, Is.Unique);
    }

    /// <summary>
    /// The override is what a self-hoster reaches for when the derived name is wrong, and it is
    /// typed by hand into a .env - so a scheme, a port or a trailing slash has to be tolerated
    /// rather than turned into a hostname nothing matches.
    /// </summary>
    [TestCase("wiki.example.com", "wiki.example.com")]
    [TestCase("https://wiki.example.com", "wiki.example.com")]
    [TestCase("https://wiki.example.com/", "wiki.example.com")]
    [TestCase("WIKI.example.com", "wiki.example.com")]
    [TestCase("wiki.example.com:8443", "wiki.example.com")]
    public void A_hand_written_domain_override_is_reduced_to_the_host_header_it_will_match(
        string configured, string expected)
    {
        Assert.That(SiteHost.Normalise(configured), Is.EqualTo(expected));
    }

    // ── One wiki, one hostname ───────────────────────────────────────────────

    [Test]
    public void The_apex_is_the_wiki_site_but_names_no_wiki()
    {
        var match = WikiHost.Match("wiki.venta.gg", "wiki.venta.gg");

        Assert.Multiple(() =>
        {
            Assert.That(match.IsWikiHost, Is.True);
            Assert.That(match.Slug, Is.Null);
        });
    }

    [TestCase("a-guild.wiki.venta.gg", "a-guild")]
    [TestCase("A-Guild.WIKI.venta.gg", "a-guild")]
    [TestCase("a-guild.wiki.venta.gg.", "a-guild")]
    public void A_label_beneath_the_apex_names_the_wiki_it_spells(string requestHost, string expected)
    {
        Assert.That(WikiHost.Match(requestHost, "wiki.venta.gg").Slug, Is.EqualTo(expected));
    }

    /// <summary>
    /// Nothing here may be mistaken for the wiki site: a lookalike host that got the security
    /// headers and a rendered page would be doing the wiki's job on somebody else's name.
    /// </summary>
    [TestCase("venta.gg")]
    [TestCase("api.venta.gg")]
    [TestCase("wiki.venta.gg.evil.example")]
    [TestCase("notwiki.venta.gg")]
    [TestCase("")]
    public void Nothing_outside_the_apex_is_the_wiki_site(string requestHost)
    {
        Assert.That(WikiHost.Match(requestHost, "wiki.venta.gg").IsWikiHost, Is.False);
    }

    /// <summary>
    /// Still the wiki site, so it gets the site's own 404 rather than falling through to the proxy -
    /// but it names no wiki, so nothing is fetched for it.
    /// </summary>
    [TestCase("www.wiki.venta.gg", TestName = "www is reserved")]
    [TestCase("deep.nested.wiki.venta.gg", TestName = "more than one label deep")]
    [TestCase("-leading.wiki.venta.gg", TestName = "leading hyphen")]
    [TestCase("under_score.wiki.venta.gg", TestName = "underscore")]
    public void A_label_that_names_no_wiki_resolves_to_nothing(string requestHost)
    {
        var match = WikiHost.Match(requestHost, "wiki.venta.gg");

        Assert.Multiple(() =>
        {
            Assert.That(match.IsWikiHost, Is.True);
            Assert.That(match.Slug, Is.Null);
        });
    }

    /// <summary>
    /// A slug is now a DNS label, which is a stricter grammar than a path segment. The vanity
    /// grammar it is minted under is already a subset of this - lowercase alphanumerics with single
    /// interior hyphens, 32 characters at most - and this is what keeps the two from drifting apart
    /// into a published wiki nobody can resolve.
    /// </summary>
    [TestCase("a-guild", true)]
    [TestCase("guild123", true)]
    [TestCase("123", true)]
    [TestCase("a", true)]
    [TestCase("-guild", false)]
    [TestCase("guild-", false)]
    [TestCase("a_guild", false)]
    [TestCase("a.guild", false)]
    [TestCase("A-Guild", false)]
    [TestCase("", false)]
    public void Only_a_valid_dns_label_can_be_a_slug(string candidate, bool expected)
    {
        Assert.That(WikiHost.IsLabel(candidate), Is.EqualTo(expected));
    }

    [Test]
    public void A_slug_may_not_outgrow_a_dns_label()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WikiHost.IsLabel(new string('a', WikiHost.MaxSlugLength)), Is.True);
            Assert.That(WikiHost.IsLabel(new string('a', WikiHost.MaxSlugLength + 1)), Is.False);
        });
    }

    /// <summary>
    /// Every site's diagnostic claims requests for its own label, and a published wiki sits one
    /// label deeper - so without a depth check, a guild whose slug is <c>admin</c> would have its
    /// hostname answered by the console's "you meant admin.venta.gg" page instead.
    /// </summary>
    [TestCase("admin.venta.gg", "admin", false, TestName = "the bound host is not misdirected")]
    [TestCase("admin.example.com", "admin", true, TestName = "a sibling name that is not bound")]
    [TestCase("admin.wiki.venta.gg", "admin", false, TestName = "a wiki one label deeper")]
    [TestCase("status.venta.gg", "admin", false, TestName = "another site's label")]
    public void Only_a_name_at_the_sites_own_depth_is_misdirected(
        string requested, string label, bool expected)
    {
        Assert.That(SiteHost.IsMisdirected(requested, $"{label}.venta.gg", label), Is.EqualTo(expected));
    }

    // ── What a moderator pastes ──────────────────────────────────────────────

    /// <summary>
    /// A report names a page in whichever form the reporter had in front of them. Normalising that
    /// by hand is how the wrong page gets taken down.
    /// </summary>
    [TestCase("a-guild", "a-guild", null)]
    [TestCase("a-guild/a-page", "a-guild", "a-page")]
    [TestCase("https://a-guild.wiki.venta.gg", "a-guild", null)]
    [TestCase("https://a-guild.wiki.venta.gg/a-page", "a-guild", "a-page")]
    [TestCase("a-guild.wiki.venta.gg/a-page", "a-guild", "a-page")]
    [TestCase("https://wiki.venta.gg/a-guild/a-page", "a-guild", "a-page")]
    [TestCase("https://wiki.venta.gg/a-guild", "a-guild", null)]
    [TestCase("  A-Guild/a-page  ", "a-guild", "a-page")]
    public void Every_form_a_reporter_might_quote_resolves_to_one_page(
        string pasted, string slug, string? pageSlug)
    {
        Assert.That(WikiAddresses.TryParse(pasted, "wiki.venta.gg", out var address), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(address.Slug, Is.EqualTo(slug));
            Assert.That(address.PageSlug, Is.EqualTo(pageSlug));
        });
    }

    /// <summary>A takedown aimed at the wrong thing is worse than a takedown that did not happen.</summary>
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("https://venta.gg/a-guild")]
    [TestCase("https://wiki.evil.example/a-guild")]
    [TestCase("a-guild/a-page/deeper")]
    [TestCase("a_guild/a-page")]
    [TestCase("https://wiki.venta.gg/")]
    public void An_address_that_names_no_page_is_refused(string pasted)
    {
        Assert.That(WikiAddresses.TryParse(pasted, "wiki.venta.gg", out _), Is.False);
    }

    // ── The policy in front of untrusted prose ───────────────────────────────

    /// <summary>
    /// Stricter than the sign-in site's, because this is the one host that renders third-party
    /// content. A read-only page needs no script, so none is permitted.
    /// </summary>
    [Test]
    public void The_policy_permits_no_script_at_all()
    {
        var policy = WikiSiteSecurity.ContentSecurityPolicy;

        Assert.Multiple(() =>
        {
            Assert.That(policy, Does.Contain("default-src 'none'"));
            Assert.That(policy, Does.Contain("script-src 'none'"));
            Assert.That(policy, Does.Contain("frame-ancestors 'none'"));
            Assert.That(policy, Does.Contain("base-uri 'none'"));
            Assert.That(policy, Does.Contain("form-action 'none'"));
            Assert.That(policy, Does.Not.Contain("unsafe-inline"));
            Assert.That(policy, Does.Not.Contain("unsafe-eval"));
        });
    }

    /// <summary>Images may come from this instance and nowhere an author picked.</summary>
    [Test]
    public void The_policy_names_an_image_allowlist_rather_than_a_wildcard()
    {
        var policy = WikiSiteSecurity.ContentSecurityPolicy;

        Assert.Multiple(() =>
        {
            Assert.That(policy, Does.Contain("img-src 'self'"));
            Assert.That(policy, Does.Not.Contain("img-src *"));
            Assert.That(policy, Does.Not.Contain("img-src https:"));
        });
    }

    // ── What a rendered document says ────────────────────────────────────────

    /// <summary>Indexing is a separate opt-in that does not exist yet.</summary>
    [Test]
    public void Every_document_asks_not_to_be_indexed()
    {
        foreach (var html in new[]
                 {
                     WikiPageRenderer.Page(Page(), Context),
                     WikiPageRenderer.Index(new PublicWiki("a-guild", "A Guild", null, []), Context),
                     WikiPageRenderer.NotFound(Context),
                 })
        {
            Assert.That(html, Does.Contain("name=\"robots\" content=\"noindex, nofollow\""));
        }
    }

    [Test]
    public void A_rendered_page_carries_no_script_of_its_own()
    {
        var html = WikiPageRenderer.Page(Page(content: "some prose"), Context);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("<script").IgnoreCase);
            Assert.That(Regex.IsMatch(html, @"\son[a-z]+\s*="), Is.False,
                "an inline handler would be blocked by the policy and fail silently anyway");
        });
    }

    /// <summary>A title is data, not markup.</summary>
    [Test]
    public void A_title_containing_markup_is_escaped()
    {
        var html = WikiPageRenderer.Page(Page(title: "<script>alert(1)</script>"), Context);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("<script").IgnoreCase);
            Assert.That(html, Does.Contain("&lt;script&gt;"));
        });
    }

    /// <summary>
    /// A slug nobody published and a slug that never existed have to be the same answer. A 403, or
    /// a different page, confirms the thing is there.
    /// </summary>
    [Test]
    public void The_not_found_document_says_nothing_about_which_case_it_is()
    {
        var html = WikiPageRenderer.NotFound(Context);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Not found"));
            Assert.That(html, Does.Not.Contain("unpublished").IgnoreCase);
            Assert.That(html, Does.Not.Contain("private").IgnoreCase);
            Assert.That(html, Does.Not.Contain("permission").IgnoreCase);
        });
    }

    /// <summary>"Nobody answered" is not a claim that the page is gone.</summary>
    [Test]
    public void An_outage_is_a_different_document_from_a_missing_page()
    {
        Assert.That(WikiPageRenderer.Unavailable(Context), Is.Not.EqualTo(WikiPageRenderer.NotFound(Context)));
    }

    // ── Nothing on the public surface is about a person ──────────────────────

    /// <summary>
    /// The authenticated WikiPageDto is a Facet over the whole entity, so it carries AuthorId,
    /// LastEditorId, GuildId, Visibility and Tags. Reusing it here would publish the user ids of
    /// everyone who ever edited an internal page. The public shapes are hand-written, and this is
    /// what keeps them that way as fields get added.
    /// </summary>
    [TestCase(typeof(PublicWiki))]
    [TestCase(typeof(PublicWikiPage))]
    [TestCase(typeof(PublicWikiPageSummary))]
    public void No_public_shape_carries_anything_about_a_person(Type shape)
    {
        var forbidden = new[]
        {
            "author", "editor", "userid", "memberid", "guildid", "visibility",
            "watcher", "reaction", "comment", "revision",
        };

        var offenders = shape.GetProperties()
            .Where(p => forbidden.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        Assert.That(offenders, Is.Empty,
            $"{shape.Name} would publish these to anyone who asks: {string.Join(", ", offenders)}");
    }

    // ── One page, one address ────────────────────────────────────────────────

    /// <summary>
    /// The apex still answers the path form and redirects it here, so a canonical pointing back at
    /// the apex would leave one page advertising two addresses.
    /// </summary>
    [Test]
    public void A_page_is_canonical_on_its_own_wikis_hostname()
    {
        var html = WikiPageRenderer.Page(Page(), Context);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain(
                "rel=\"canonical\" href=\"https://a-guild.wiki.venta.gg/a-page-12345678\""));
            Assert.That(html, Does.Contain(
                "property=\"og:url\" content=\"https://a-guild.wiki.venta.gg/a-page-12345678\""));
        });
    }

    [Test]
    public void An_index_is_canonical_on_its_own_wikis_hostname()
    {
        var html = WikiPageRenderer.Index(new PublicWiki("a-guild", "A Guild", null, []), Context);

        Assert.That(html, Does.Contain("rel=\"canonical\" href=\"https://a-guild.wiki.venta.gg\""));
    }

    /// <summary>Every link inside a wiki is now relative to that wiki's own host.</summary>
    [Test]
    public void An_index_links_its_pages_without_repeating_the_slug()
    {
        var html = WikiPageRenderer.Index(
            new PublicWiki("a-guild", "A Guild", null,
                [new PublicWikiPageSummary("a-page-12345678", "A page", null, null)]),
            Context);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("href=\"/a-page-12345678\""));
            Assert.That(html, Does.Not.Contain("href=\"/a-guild/a-page-12345678\""));
        });
    }

    /// <summary>
    /// The reader has no account, no session and no script on this host, so the address has to
    /// travel with the link or the report arrives naming nothing a moderator can act on.
    /// </summary>
    [Test]
    public void The_report_link_carries_the_page_it_is_about()
    {
        var html = WikiPageRenderer.Page(Page(), Context);

        Assert.That(html, Does.Contain(
            "https://support.venta.gg/contact?wiki=a-guild%2Fa-page-12345678"));
    }

    /// <summary>The stylesheet the endpoint serves has to exist.</summary>
    [Test]
    public void The_stylesheet_ships_with_the_gateway()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Echo", "wwwroot")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "could not locate Echo/wwwroot from the test directory");
        Assert.That(File.Exists(Path.Combine(directory!.FullName, "Echo", "wwwroot", "wiki", "wiki.css")), Is.True);
    }
}
