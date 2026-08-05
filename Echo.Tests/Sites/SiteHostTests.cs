using Echo.Docs;
using Echo.Sites;

namespace Echo.Tests.Sites;

/// <summary>Pins the hostname derivation the docs, admin and support sites all share.</summary>
[TestFixture]
[Category("Unit")]
public class SiteHostTests
{
    // ── A site is a sibling of the API, not a child of it ────────────────────

    /// <summary>The regression case.</summary>
    [TestCase("admin", "https://api.venta.gg", "admin.venta.gg")]
    [TestCase("support", "https://api.venta.gg", "support.venta.gg")]
    [TestCase("status", "https://api.venta.gg", "status.venta.gg")]
    [TestCase("docs", "https://api.venta.gg", "docs.venta.gg")]
    [TestCase("admin", "https://chat.example.com", "admin.example.com")]
    [TestCase("support", "https://chat.example.com", "support.example.com")]
    [TestCase("status", "https://chat.example.com", "status.example.com")]
    public void An_instance_on_a_subdomain_has_its_first_label_replaced(
        string label, string instanceUrl, string expected)
    {
        Assert.That(SiteHost.DeriveFrom(label, instanceUrl), Is.EqualTo(expected));
    }

    /// <summary>A bare registrable domain has nothing to replace, so the label is prepended.</summary>
    [TestCase("admin", "https://venta.gg", "admin.venta.gg")]
    [TestCase("support", "https://example.com", "support.example.com")]
    [TestCase("status", "https://venta.gg", "status.venta.gg")]
    [TestCase("admin", "http://localhost:8080", "admin.localhost")]
    [TestCase("support", "http://localhost:8080", "support.localhost")]
    [TestCase("status", "http://localhost:8080", "status.localhost")]
    public void A_bare_domain_gets_the_label_prepended(string label, string instanceUrl, string expected)
    {
        Assert.That(SiteHost.DeriveFrom(label, instanceUrl), Is.EqualTo(expected));
    }

    /// <summary>An address has nothing sensible to derive from, so it is prefixed rather than
    /// having its first octet eaten - <c>admin.168.1.10</c> would be a name that resolves nowhere
    /// and looks like a typo forever.</summary>
    [TestCase("admin", "http://192.168.1.10:8080", "admin.192.168.1.10")]
    [TestCase("support", "https://10.0.0.5", "support.10.0.0.5")]
    public void An_address_is_prefixed_rather_than_having_a_label_replaced(
        string label, string instanceUrl, string expected)
    {
        Assert.That(SiteHost.DeriveFrom(label, instanceUrl), Is.EqualTo(expected));
    }

    /// <summary>Already on the site's own host: left alone, not turned into
    /// <c>admin.admin.venta.gg</c>.</summary>
    [Test]
    public void An_instance_already_on_the_site_host_is_left_alone()
    {
        Assert.That(SiteHost.DeriveFrom("admin", "https://admin.venta.gg"), Is.EqualTo("admin.venta.gg"));
    }

    // ── A broken instance URL must not take the gateway down ─────────────────

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not a url")]
    [TestCase("venta.gg")]          // no scheme, so not an absolute Uri
    public void A_misconfigured_instance_url_falls_back_rather_than_throwing(string? instanceUrl)
    {
        Assert.That(SiteHost.DeriveFrom("admin", instanceUrl), Is.EqualTo("admin.localhost"));
    }

    // ── Normalise: what an operator actually types ───────────────────────────

    /// <summary>The gating compares against <c>Request.Host.Host</c>, which carries no scheme, port
    /// or path. Reducing these rather than rejecting them is the difference between a working
    /// deployment and a 404 identical to leaving the variable unset.</summary>
    [TestCase("admin.venta.gg", "admin.venta.gg")]
    [TestCase("https://admin.venta.gg", "admin.venta.gg")]
    [TestCase("https://admin.venta.gg/", "admin.venta.gg")]
    [TestCase("https://admin.venta.gg/some/path", "admin.venta.gg")]
    [TestCase("http://admin.venta.gg:8080", "admin.venta.gg")]
    [TestCase("admin.venta.gg/", "admin.venta.gg")]
    [TestCase("admin.venta.gg:8080", "admin.venta.gg")]
    [TestCase("  Admin.Venta.GG  ", "admin.venta.gg")]
    public void Normalise_reduces_a_configured_value_to_a_bare_host(string configured, string expected)
    {
        Assert.That(SiteHost.Normalise(configured), Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("/")]
    public void Normalise_treats_an_empty_value_as_unset(string? configured)
    {
        Assert.That(SiteHost.Normalise(configured), Is.Null);
    }

    // ── The docs site's own API is unchanged ─────────────────────────────────

    /// <summary><c>DocsEndpoints</c> now forwards to <see cref="SiteHost"/>.</summary>
    [TestCase("https://api.venta.gg", "docs.venta.gg")]
    [TestCase("https://venta.gg", "docs.venta.gg")]
    [TestCase("http://localhost:8080", "docs.localhost")]
    [TestCase("https://docs.venta.gg", "docs.venta.gg")]
    public void DocsEndpoints_still_derives_what_it_always_did(string instanceUrl, string expected)
    {
        Assert.That(DocsEndpoints.DeriveFrom(instanceUrl), Is.EqualTo(expected));
    }

    [Test]
    public void DocsEndpoints_normalise_still_forwards()
    {
        Assert.That(DocsEndpoints.Normalise("https://docs.venta.gg:8443/"), Is.EqualTo("docs.venta.gg"));
    }

    // ── The three sites never collide ────────────────────────────────────────

    /// <summary>Each site must land on its own name.</summary>
    [TestCase("https://api.venta.gg")]
    [TestCase("https://venta.gg")]
    [TestCase("http://localhost:8080")]
    [TestCase("http://192.168.1.10:8080")]
    public void The_three_sites_never_derive_the_same_host(string instanceUrl)
    {
        var hosts = new[]
        {
            SiteHost.DeriveFrom("docs", instanceUrl),
            SiteHost.DeriveFrom(SiteHosting.AdminLabel, instanceUrl),
            SiteHost.DeriveFrom(SiteHosting.SupportLabel, instanceUrl),
        };

        Assert.That(hosts, Is.Unique);
    }

    /// <summary>The scheme comes from the instance URL rather than being assumed.</summary>
    [Test]
    public void BaseUrl_takes_its_scheme_from_the_instance_url()
    {
        var url = SiteHost.BaseUrl("support.venta.gg");

        Assert.That(url, Does.StartWith("http"));
        Assert.That(url, Does.EndWith("://support.venta.gg"));
    }
}
