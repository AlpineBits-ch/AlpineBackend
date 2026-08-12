using AppEnvironment;

namespace Echo.Tests.Cors;

/// <summary>
/// The origin list is the whole of what <c>AlpinePolicy</c> decides, and every mistake it can make is
/// invisible server-side: the request succeeds, the browser throws the response away, and no log line
/// is written anywhere. So each rule gets pinned here rather than eyeballed.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ClientOriginsTests
{
    private const string DefaultInstance = "https://api.venta.gg";

    /// <summary>The one that would take the public web client down.</summary>
    [Test]
    public void The_official_web_origin_is_allowed_by_default()
    {
        Assert.That(ClientOrigins.Resolve(DefaultInstance, null), Contains.Item("https://app.venta.gg"));
    }

    /// <summary>A self-hoster builds their own web image and serves their own origin.</summary>
    [Test]
    public void A_self_hosted_instance_gets_its_own_web_origin()
    {
        var origins = ClientOrigins.Resolve("https://api.example.com", null);

        Assert.That(origins, Contains.Item("https://app.example.com"));
    }

    /// <summary>And not ours.</summary>
    [Test]
    public void A_self_hosted_instance_does_not_allow_the_venta_web_origin()
    {
        var origins = ClientOrigins.Resolve("https://api.example.com", null);

        Assert.That(origins, Has.No.Member("https://app.venta.gg"));
    }

    /// <summary>The scheme comes from the instance rather than being assumed, so a self-hosted
    /// instance on plain HTTP is not handed an https origin that never matches.</summary>
    [Test]
    public void The_derived_origin_keeps_the_instance_scheme()
    {
        Assert.That(ClientOrigins.Resolve("http://api.example.com", null),
            Contains.Item("http://app.example.com"));
    }

    [Test]
    public void Configured_origins_are_added()
    {
        var origins = ClientOrigins.Resolve(DefaultInstance, "https://venta.example.org");

        Assert.That(origins, Contains.Item("https://venta.example.org"));
    }

    /// <summary>Commas, semicolons and whitespace all separate, because an operator writing a list
    /// into an env var will use whichever they are used to.</summary>
    [TestCase("https://a.example, https://b.example")]
    [TestCase("https://a.example;https://b.example")]
    [TestCase("https://a.example https://b.example")]
    [TestCase("https://a.example,\n  https://b.example")]
    public void Configured_origins_split_on_any_separator(string configured)
    {
        var origins = ClientOrigins.Resolve(DefaultInstance, configured);

        Assert.That(origins, Contains.Item("https://a.example"));
        Assert.That(origins, Contains.Item("https://b.example"));
    }

    /// <summary>Additive only.</summary>
    [Test]
    public void Configuring_origins_does_not_remove_the_desktop_ones()
    {
        var origins = ClientOrigins.Resolve(DefaultInstance, "https://venta.example.org");

        Assert.That(origins, Contains.Item("tauri://localhost"));
        Assert.That(origins, Contains.Item("http://tauri.localhost"));
    }

    /// <summary>The one that must never be accepted.</summary>
    [Test]
    public void A_wildcard_origin_is_refused()
    {
        var origins = ClientOrigins.Resolve(DefaultInstance, "*");

        Assert.That(origins, Has.No.Member("*"));
        Assert.That(ClientOrigins.Rejects("*"), Contains.Item("*"));
    }

    [Test]
    public void A_wildcard_among_valid_entries_does_not_take_them_with_it()
    {
        var origins = ClientOrigins.Resolve(DefaultInstance, "*, https://ok.example");

        Assert.That(origins, Has.No.Member("*"));
        Assert.That(origins, Contains.Item("https://ok.example"));
    }

    /// <summary>
    /// Wildcard subdomains are refused too, and for a different reason than <c>*</c>: ASP.NET only
    /// honours them when <c>SetIsOriginAllowedToAllowWildcardSubdomains()</c> is called, which
    /// AlpineCors does not, so an entry like this is not dangerous - it is inert.
    /// </summary>
    [TestCase("https://*.venta.gg")]
    [TestCase("https://*")]
    public void A_wildcard_host_is_refused_and_reported(string configured)
    {
        Assert.That(ClientOrigins.Resolve(DefaultInstance, configured),
            Is.EqualTo(ClientOrigins.Resolve(DefaultInstance, null)));
        Assert.That(ClientOrigins.Rejects(configured), Contains.Item(configured));
    }

    /// <summary>Every case here is an entry that looks right and can never match.</summary>
    [TestCase("https://client.example.test/", "https://client.example.test", TestName = "trailing slash is trimmed")]
    [TestCase("https://Client.Example.TEST", "https://client.example.test", TestName = "host is lower-cased")]
    [TestCase("HTTPS://client.example.test", "https://client.example.test", TestName = "scheme is lower-cased")]
    // Documents Uri's own canonicalisation rather than ours, and is why Normalise does no
    // lower-casing of its own: it holds for a custom scheme too, not just http/https.
    [TestCase("TAURI://Client.Example.Test", "tauri://client.example.test", TestName = "a custom scheme is lower-cased too")]
    [TestCase("tauri://client.example.test:1420", "tauri://client.example.test:1420", TestName = "a custom scheme keeps an explicit port")]
    [TestCase("https://client.example.test:8443", "https://client.example.test:8443", TestName = "an explicit port is kept")]
    [TestCase("https://client.example.test:443", "https://client.example.test", TestName = "a default port is dropped")]
    public void An_origin_is_normalised_the_way_a_browser_sends_it(string configured, string expected)
    {
        var origins = ClientOrigins.Resolve(DefaultInstance, configured);

        Assert.That(origins, Contains.Item(expected));
        if (configured != expected) Assert.That(origins, Has.No.Member(configured));
    }

    /// <summary>A path, query or fragment means somebody pasted a URL.</summary>
    [TestCase("https://app.venta.gg/login")]
    [TestCase("https://app.venta.gg/?next=/overview")]
    [TestCase("https://app.venta.gg/#/overview")]
    [TestCase("https://user:pass@app.venta.gg")]
    // No scheme at all - the single most likely thing for an operator to type.
    [TestCase("app.venta.gg")]
    // Whitespace is a separator, so an unparseable entry has to be one token to be one entry.
    [TestCase("://app.venta.gg")]
    [TestCase("https://")]
    public void An_entry_that_is_not_an_origin_is_rejected_and_reported(string configured)
    {
        Assert.That(ClientOrigins.Resolve(DefaultInstance, configured),
            Is.EqualTo(ClientOrigins.Resolve(DefaultInstance, null)));
        Assert.That(ClientOrigins.Rejects(configured), Contains.Item(configured));
    }

    /// <summary>
    /// The packaged desktop webview reports these, and they are the reason normalisation cannot
    /// assume http/https: <c>Uri</c> gives an unknown scheme no default port, and printing the -1 it
    /// reports would produce <c>tauri://localhost:-1</c>, which matches nothing.
    /// </summary>
    [TestCase("tauri://localhost")]
    [TestCase("http://tauri.localhost")]
    public void The_desktop_webview_origins_survive_normalisation(string origin)
    {
        Assert.That(ClientOrigins.Resolve(DefaultInstance, null), Contains.Item(origin));
        Assert.That(ClientOrigins.Rejects(origin), Is.Empty);
    }

    [Test]
    public void Duplicates_collapse()
    {
        var origins = ClientOrigins.Resolve(DefaultInstance, "https://app.venta.gg, https://APP.venta.gg/");

        Assert.That(origins.Count(o => o == "https://app.venta.gg"), Is.EqualTo(1));
    }

    /// <summary>An unparseable INSTANCE_URL must not take the process down at startup - the same
    /// guarantee InstanceHosts makes. It costs the derived origin, nothing else.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-url")]
    public void A_broken_instance_url_still_yields_the_built_in_origins(string? instanceUrl)
    {
        var origins = ClientOrigins.Resolve(instanceUrl, null);

        Assert.That(origins, Contains.Item("http://localhost:1420"));
        Assert.That(origins, Contains.Item("tauri://localhost"));
    }

    [Test]
    public void Nothing_configured_rejects_nothing()
    {
        Assert.That(ClientOrigins.Rejects(null), Is.Empty);
        Assert.That(ClientOrigins.Rejects(""), Is.Empty);
    }
}
