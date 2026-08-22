using Discovery.Api;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests.Bus;

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

        var resolver = provider.GetRequiredService<EntitlementResolver>();
        var set = await resolver.ResolveAsync(EntitlementSubject.ForGuild("gld_wiring_check"));

        Assert.That(set.Flag(EntitlementKeys.GuildPublicListing), Is.True,
            "self-host is supposed to grant everything - false here means the resolver answered " +
            "from an empty source list instead of SelfHostEverythingSource");
    }
}
