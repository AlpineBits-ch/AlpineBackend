using Echo.Docs;
using Echo.Proxy;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>Pins the declared-path to public-path mapping the docs aggregator applies.</summary>
[TestFixture]
[Category("Unit")]
public class DocsPathMappingTests
{
    private static DocsService Service(string name) =>
        DocsCatalog.Services.Single(s => s.Name == name);

    // ── The service segment is inserted, not prepended ───────────────────────

    [TestCase("guild", "/api/v1/inbox/unread", "/api/v1/guild/inbox/unread")]
    [TestCase("guild", "/api/v1/guilds/{guildId}/icon", "/api/v1/guild/guilds/{guildId}/icon")]
    [TestCase("messaging", "/api/v1/conversations", "/api/v1/messaging/conversations")]
    [TestCase("social", "/api/v1/relationships", "/api/v1/social/relationships")]
    [TestCase("isle", "/api/v1/voice/join", "/api/v1/isle/voice/join")]
    [TestCase("imports", "/api/v1/links", "/api/v1/imports/links")]
    public void Rewritten_routes_get_the_service_segment_after_api_v1(
        string service, string declared, string expected)
    {
        Assert.That(Service(service).ToPublicPath(declared), Is.EqualTo(expected));
    }

    /// <summary>Identity and Federation declare several routes without a leading slash.</summary>
    [TestCase("identity", "api/v1/devices", "/api/v1/identity/devices")]
    [TestCase("identity", "api/v1/admin/mls-certificate-coverage", "/api/v1/identity/admin/mls-certificate-coverage")]
    [TestCase("federation", "api/v1/federation/events", "/api/v1/federation/events")]
    public void Routes_declared_without_a_leading_slash_still_map(
        string service, string declared, string expected)
    {
        Assert.That(Service(service).ToPublicPath(declared), Is.EqualTo(expected));
    }

    // ── Pass-through routes keep their declared path ─────────────────────────

    /// <summary>For these the public path *is* the contract - a remote instance, a Discord bot
    /// library or an existing webhook integration is pointed straight at it - so ProxyConfig routes
    /// them with no transform and the docs must not invent a prefix.</summary>
    [TestCase("federation", "/api/v1/federation/events")]
    [TestCase("federation", "/api/v1/admin/federation/instances")]
    [TestCase("federation", "/.well-known/federation/handshake")]
    [TestCase("bots", "/api/discord/v10/channels/{channelId}/messages")]
    [TestCase("guild", "/api/webhooks/{webhookId}/{token}")]
    [TestCase("identity", "/connect/token")]
    [TestCase("identity", "/.well-known/openid-configuration")]
    public void Pass_through_routes_are_published_exactly_as_declared(string service, string declared)
    {
        Assert.That(Service(service).ToPublicPath(declared), Is.EqualTo(declared));
    }

    /// <summary>Bots serves both surfaces: its own API is rewritten, the Discord-compat one is not.</summary>
    [Test]
    public void Bots_rewrites_its_own_api_but_not_the_discord_surface()
    {
        var bots = Service("bots");

        Assert.Multiple(() =>
        {
            Assert.That(bots.ToPublicPath("/api/v1/applications"), Is.EqualTo("/api/v1/bots/applications"));
            Assert.That(bots.ToPublicPath("/api/discord/v10/users/@me"), Is.EqualTo("/api/discord/v10/users/@me"));
        });
    }

    // ── Unreachable routes are dropped, not published ────────────────────────

    /// <summary>The document endpoint the aggregator itself reads.</summary>
    [Test]
    public void The_internal_document_path_is_not_published()
    {
        Assert.That(Service("guild").ToPublicPath("/internal/openapi/v1.json"), Is.Null);
    }

    [TestCase("guild", "/guild/health")]
    [TestCase("social", "/some/unrouted/path")]
    public void Routes_the_gateway_does_not_expose_are_dropped(string service, string declared)
    {
        Assert.That(Service(service).ToPublicPath(declared), Is.Null);
    }

    /// <summary>Federation has no rewriting route at all, so anything outside its pass-through
    /// prefixes is unreachable rather than silently prefixed.</summary>
    [Test]
    public void Federation_has_no_rewrite_and_drops_anything_outside_its_protocol_paths()
    {
        Assert.That(Service("federation").ToPublicPath("/api/v1/something-else"), Is.Null);
    }

    // ── The catalogue must not drift from the routing table ──────────────────

    /// <summary>Every cluster the docs catalogue names has to exist in ProxyConfig, or the
    /// aggregator will silently fetch nothing for that service.</summary>
    [Test]
    public void Every_catalogued_cluster_exists_in_the_proxy_configuration()
    {
        var clusters = ProxyConfig.GetClusters().Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            foreach (var service in DocsCatalog.Services)
                Assert.That(clusters, Does.Contain(service.Cluster), $"{service.Name} points at a cluster that does not exist");
        });
    }

    // ── The docs hostname ────────────────────────────────────────────────────

    /// <summary>The docs site is a sibling of the API, not a child of it.</summary>
    [TestCase("https://api.venta.gg", "docs.venta.gg")]
    [TestCase("https://chat.example.com", "docs.example.com")]
    [TestCase("https://venta.gg", "docs.venta.gg")]
    [TestCase("https://example.com", "docs.example.com")]
    [TestCase("http://localhost:8080", "docs.localhost")]
    [TestCase("https://docs.venta.gg", "docs.venta.gg")]
    public void The_docs_host_is_derived_as_a_sibling_of_the_instance(string instanceUrl, string expected)
    {
        Assert.That(DocsEndpoints.DeriveFrom(instanceUrl), Is.EqualTo(expected));
    }

    /// <summary>An IP-addressed instance has no domain to derive a sibling from.</summary>
    [TestCase("http://192.168.1.10:8080", "docs.192.168.1.10")]
    public void An_ip_instance_gets_a_prefix_rather_than_a_mangled_label(string instanceUrl, string expected)
    {
        Assert.That(DocsEndpoints.DeriveFrom(instanceUrl), Is.EqualTo(expected));
    }

    /// <summary>
    /// DOCS_DOMAIN is compared against <c>Request.Host.Host</c>, which carries no scheme, port or
    /// path - so a value written as a URL has to be reduced rather than matched literally, or it
    /// 404s exactly like leaving it unset.
    /// </summary>
    [TestCase("docs.venta.gg", "docs.venta.gg")]
    [TestCase("https://docs.venta.gg", "docs.venta.gg")]
    [TestCase("https://docs.venta.gg/", "docs.venta.gg")]
    [TestCase("http://docs.venta.gg:8080", "docs.venta.gg")]
    [TestCase("docs.venta.gg/", "docs.venta.gg")]
    [TestCase("docs.venta.gg:8080", "docs.venta.gg")]
    [TestCase("  Docs.Venta.GG  ", "docs.venta.gg")]
    public void A_docs_domain_written_as_a_url_is_reduced_to_its_hostname(string configured, string expected)
    {
        Assert.That(DocsEndpoints.Normalise(configured), Is.EqualTo(expected));
    }

    /// <summary>An unset or blank value must fall through to the derivation, not bind to "".</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void A_blank_docs_domain_falls_through_to_the_derivation(string? configured)
    {
        Assert.That(DocsEndpoints.Normalise(configured), Is.Null);
    }

    /// <summary>A broken INSTANCE_URL must not stop the gateway from starting.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-url")]
    public void A_malformed_instance_url_falls_back_instead_of_throwing(string? instanceUrl)
    {
        Assert.That(DocsEndpoints.DeriveFrom(instanceUrl), Is.EqualTo("docs.localhost"));
    }

    /// <summary>Each rewriting service must actually have the proxy route the mapping assumes.</summary>
    [Test]
    public void Every_rewrite_prefix_has_a_matching_proxy_route()
    {
        var routePaths = ProxyConfig.GetRoutes()
            .Select(r => r.Match.Path)
            .Where(p => p is not null)
            .ToList();

        Assert.Multiple(() =>
        {
            foreach (var service in DocsCatalog.Services.Where(s => s.RewriteAs is not null))
            {
                var expected = $"{service.RewriteAs}/{{**catch-all}}";
                Assert.That(routePaths, Does.Contain(expected),
                    $"{service.Name} assumes a rewrite the proxy does not perform");
            }
        });
    }
}
