using AppEnvironment;
using Identity.Application.Services.Steam;

namespace Identity.Tests.Services;

/// <summary>Where a finished Steam flow may put the browser.</summary>
[TestFixture]
[Category("Unit")]
public class SteamReturnTargetsTests
{
    private string _originalApp = null!;
    private string _originalInstance = null!;

    [SetUp]
    public void CaptureEnv()
    {
        _originalApp = Environment.GetEnvironmentVariable(WebClientHost.EnvironmentVariable) ?? string.Empty;
        _originalInstance = Env.GeneralConfiguration.InstanceUrl;
    }

    [TearDown]
    public void RestoreEnv()
    {
        Environment.SetEnvironmentVariable(
            WebClientHost.EnvironmentVariable,
            string.IsNullOrEmpty(_originalApp) ? null : _originalApp);
        Env.GeneralConfiguration.InstanceUrl = _originalInstance;
    }

    /// <summary>The desktop and mobile flow must be untouched: absent, empty and blank all fall back to
    /// the configured deep link.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void An_absent_target_falls_back_to_the_configured_default(string? requested)
    {
        Assert.That(SteamReturnTargets.Resolve(requested), Is.EqualTo(SteamReturnTargets.Default));
    }

    /// <summary>The whole point of the change.</summary>
    [Test]
    public void The_web_client_page_is_allowed()
    {
        var target = WebClientHost.Link(WebClientHost.SteamAuthPath);

        Assert.That(SteamReturnTargets.Resolve(target), Is.EqualTo(target));
    }

    /// <summary>An https URL, not a custom scheme - the failure that made this necessary.</summary>
    [Test]
    public void The_web_client_target_is_a_url_a_browser_can_follow()
    {
        Assert.That(SteamReturnTargets.WebClient, Does.StartWith("https://"));
        Assert.That(SteamReturnTargets.WebClient, Does.EndWith(WebClientHost.SteamAuthPath));
    }

    /// <summary>It tracks <c>APP_DOMAIN</c> rather than being a literal, so a deployment that moves its
    /// web client does not silently keep redirecting to the one it moved off.</summary>
    [Test]
    public void The_web_client_target_follows_the_configured_web_host()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, "app.somewhere-else.test");

        Assert.That(SteamReturnTargets.WebClient, Is.EqualTo("https://app.somewhere-else.test/steam-auth"));
        Assert.That(SteamReturnTargets.Allowed, Contains.Item(SteamReturnTargets.WebClient));
    }

    /// <summary>
    /// Exact match, not a prefix or host-suffix comparison - which is how open redirects get
    /// written.
    /// </summary>
    [TestCase("https://app.venta.gg.attacker.example/steam-auth")]
    [TestCase("https://attacker.example/?next=https://app.venta.gg/steam-auth")]
    [TestCase("venta://steam-auth.attacker.example")]
    [TestCase("https://app.venta.gg/steam-auth/../../evil")]
    [TestCase("https://app.venta.gg/steam-auth.evil")]
    [TestCase("javascript:alert(1)")]
    [TestCase("//attacker.example")]
    public void An_unrecognised_target_is_refused(string requested)
    {
        Assert.That(SteamReturnTargets.Resolve(requested), Is.EqualTo(SteamReturnTargets.Default));
    }

    /// <summary>A prefix of an allowed target is not an allowed target.</summary>
    [Test]
    public void A_target_that_merely_starts_with_an_allowed_one_is_refused()
    {
        var attacker = WebClientHost.Link(WebClientHost.SteamAuthPath) + "@attacker.example";

        Assert.That(SteamReturnTargets.Resolve(attacker), Is.EqualTo(SteamReturnTargets.Default));
    }

    /// <summary>Three, and only three.</summary>
    [Test]
    public void Exactly_the_deep_link_the_auth_site_and_the_web_client_are_allowed()
    {
        Assert.That(SteamReturnTargets.Allowed, Is.EquivalentTo(new[]
        {
            SteamReturnTargets.Default,
            SteamReturnTargets.AuthSite,
            WebClientHost.Link(WebClientHost.SteamAuthPath),
        }));
    }

    /// <summary>The auth site keeps working - it is the target the SSO flow has been using since before
    /// the web client existed.</summary>
    [Test]
    public void The_auth_site_page_is_still_allowed()
    {
        Assert.That(SteamReturnTargets.Resolve(SteamReturnTargets.AuthSite),
            Is.EqualTo(SteamReturnTargets.AuthSite));
    }
}
