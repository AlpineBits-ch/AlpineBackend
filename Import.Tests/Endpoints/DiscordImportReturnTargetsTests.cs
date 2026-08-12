using AppEnvironment;
using Import.Application.Endpoints;

namespace Import.Tests.Endpoints;

/// <summary>Where a finished Discord import may put the browser.</summary>
[TestFixture]
[Category("Unit")]
public class DiscordImportReturnTargetsTests
{
    /// <summary>The desktop flow must be untouched: absent, unknown and empty all fall back to the
    /// configured deep link.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void An_absent_target_falls_back_to_the_configured_default(string? requested)
    {
        Assert.That(DiscordImportReturnTargets.Resolve(requested),
            Is.EqualTo(DiscordImportReturnTargets.Default));
    }

    [Test]
    public void The_web_client_page_is_allowed()
    {
        var target = WebClientHost.Link(WebClientHost.DiscordImportPath);

        Assert.That(DiscordImportReturnTargets.Resolve(target), Is.EqualTo(target));
    }

    /// <summary>
    /// Exact match, not a prefix or host-suffix comparison - which is how open redirects get
    /// written.
    /// </summary>
    [TestCase("https://app.venta.gg.attacker.example/discord-import")]
    [TestCase("https://attacker.example/?x=https://app.venta.gg/discord-import")]
    [TestCase("venta://discord-import.attacker.example")]
    [TestCase("https://app.venta.gg/discord-import/../../evil")]
    [TestCase("javascript:alert(1)")]
    public void An_unrecognised_target_is_refused(string requested)
    {
        Assert.That(DiscordImportReturnTargets.Resolve(requested),
            Is.EqualTo(DiscordImportReturnTargets.Default));
    }

    /// <summary>Two, and only two. A third appearing here without a reason is the review signal.</summary>
    [Test]
    public void Exactly_the_deep_link_and_the_web_client_are_allowed()
    {
        Assert.That(DiscordImportReturnTargets.Allowed, Is.EquivalentTo(new[]
        {
            DiscordImportReturnTargets.Default,
            WebClientHost.Link(WebClientHost.DiscordImportPath),
        }));
    }
}
