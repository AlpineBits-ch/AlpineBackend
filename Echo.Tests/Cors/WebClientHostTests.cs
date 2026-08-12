using AppEnvironment;

namespace Echo.Tests.Cors;

/// <summary>The web client's host, and the invariant that ties it to the CORS allowlist.</summary>
[TestFixture]
[Category("Unit")]
public class WebClientHostTests
{
    private string? _originalApp;
    private string _originalInstance = null!;

    [SetUp]
    public void CaptureEnv()
    {
        _originalApp = Environment.GetEnvironmentVariable(WebClientHost.EnvironmentVariable);
        _originalInstance = Env.GeneralConfiguration.InstanceUrl;
    }

    [TearDown]
    public void RestoreEnv()
    {
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, _originalApp);
        Env.GeneralConfiguration.InstanceUrl = _originalInstance;
    }

    [Test]
    public void The_default_web_client_is_the_api_host_sibling()
    {
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";

        Assert.That(WebClientHost.BaseUrl, Is.EqualTo("https://app.venta.gg"));
    }

    [Test]
    public void A_self_hosted_instance_derives_its_own_web_client()
    {
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, null);
        Env.GeneralConfiguration.InstanceUrl = "https://api.example.com";

        Assert.That(WebClientHost.BaseUrl, Is.EqualTo("https://app.example.com"));
    }

    /// <summary>
    /// Both spellings, because an operator setting <c>ADMIN_DOMAIN</c> writes a hostname and one
    /// setting <c>INSTANCE_URL</c> writes a URL - and this variable sits between the two.
    /// </summary>
    [TestCase("app.example.org", "https://app.example.org", TestName = "a bare hostname")]
    [TestCase("https://app.example.org", "https://app.example.org", TestName = "a full URL")]
    [TestCase("https://app.example.org/", "https://app.example.org", TestName = "a trailing slash")]
    [TestCase("https://app.example.org:8443", "https://app.example.org:8443", TestName = "an explicit port")]
    public void An_override_is_accepted_as_a_hostname_or_a_url(string configured, string expected)
    {
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, configured);

        Assert.That(WebClientHost.BaseUrl, Is.EqualTo(expected));
    }

    /// <summary>A bare hostname takes the instance's scheme rather than assuming https, so a self-hosted
    /// instance on plain HTTP is not handed an address that does not answer.</summary>
    [Test]
    public void A_bare_hostname_override_takes_the_instance_scheme()
    {
        Env.GeneralConfiguration.InstanceUrl = "http://api.example.com";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, "app.example.com");

        Assert.That(WebClientHost.BaseUrl, Is.EqualTo("http://app.example.com"));
    }

    /// <summary>The invariant.</summary>
    [Test]
    public void The_web_client_host_is_always_an_allowed_origin()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://api.venta.gg";
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, "app.somewhere-else.test");

        Assert.That(ClientOrigins.Allowed, Contains.Item(WebClientHost.BaseUrl));
    }

    /// <summary>The paths are the client's, not ours.</summary>
    [Test]
    public void The_deep_link_paths_match_the_custom_scheme_ones()
    {
        Assert.That(WebClientHost.SteamAuthPath, Is.EqualTo("/steam-auth"));
        Assert.That(WebClientHost.DiscordImportPath, Is.EqualTo("/discord-import"));
        Assert.That(WebClientHost.InstallBotPath, Is.EqualTo("/install-bot"));
        Assert.That(WebClientHost.InvitePath, Is.EqualTo("/invite"));
    }

    [Test]
    public void A_link_is_absolute_with_no_double_slash()
    {
        Environment.SetEnvironmentVariable(WebClientHost.EnvironmentVariable, "https://app.venta.gg/");

        Assert.That(WebClientHost.Link(WebClientHost.SteamAuthPath),
            Is.EqualTo("https://app.venta.gg/steam-auth"));
    }
}
