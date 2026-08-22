using Discovery.Api;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests.Bus;

/// <summary>
/// Discovery.Application/Program.cs shipped for a while with no entitlement wiring at all - see the
/// task 11 report. Every unit test that hand-constructs an EntitlementResolver stayed green through
/// that, because none of them go through the container the way Program.cs does. This one does, via
/// the same DiscoveryEntitlements.AddDiscoveryEntitlements Program.cs calls, so the two cannot
/// register a different set of sources.
/// </summary>
[TestFixture]
public class EntitlementResolverWiringTests
{
    [Test]
    public async Task The_entitlement_resolver_resolves_through_a_real_service_provider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddDiscoveryEntitlements(configuration);

        using var provider = services.BuildServiceProvider();

        // GetRequiredService is the first assertion: Program.cs went a while with no entitlement
        // wiring at all, and every hand-constructed unit test in this suite stayed green through it
        // because none of them resolve through a container.
        var resolver = provider.GetRequiredService<EntitlementResolver>();

        var set = await resolver.ResolveAsync(EntitlementSubject.ForGuild("gld_wiring_check"));

        // Not just "it did not throw" - this sandbox runs self-host (no LICENSE_MODE set), which
        // grants everything, so a resolver sitting on zero sources (the AddEntitlementCache-without-
        // AddEntitlements trap flagged in the task 11 report, where every key falls through to its
        // catalogue default of false) would fail this specific assertion even though
        // GetRequiredService above would still have succeeded.
        Assert.That(set.Flag(EntitlementKeys.GuildPublicListing), Is.True,
            "self-host is supposed to grant everything - false here means the resolver answered " +
            "from an empty source list instead of SelfHostEverythingSource");
    }
}
