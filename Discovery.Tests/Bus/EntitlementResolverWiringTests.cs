using Echo.Entitlements;
using Echo.Entitlements.Caching;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests.Bus;

/// <summary>
/// Discovery.Application/Program.cs shipped for a while with no entitlement wiring at all - see the
/// task 11 report. Every unit test that hand-constructs an EntitlementResolver stayed green through
/// that, because none of them go through the container the way Program.cs does. This one does.
/// </summary>
[TestFixture]
public class EntitlementResolverWiringTests
{
    /// <summary>The self-host branch of Program.cs's wiring: AddEntitlements, AddLicenseMode with no
    /// ceilings, AddEntitlementCache - the Billing-backed sources are skipped the same way Program.cs
    /// skips them when Env.License.IsHosted is false. Duplicated here rather than shared with
    /// Program.cs, so a change to one will not automatically show up as a failure in the other - see
    /// the task 11 report for why that was left to a ruling rather than decided here.</summary>
    private static ServiceProvider BuildSelfHostProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddEntitlements(configuration);
        services.AddLicenseMode(LicenseMode.SelfHost, OperatorCeilings.None);
        services.AddEntitlementCache();

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task The_entitlement_resolver_resolves_through_a_real_service_provider()
    {
        using var provider = BuildSelfHostProvider();

        // GetRequiredService is the assertion: Program.cs went a while with no AddEntitlements, no
        // AddLicenseMode and no AddEntitlementCache, and every hand-constructed unit test in this
        // suite stayed green through it because none of them resolve through a container.
        var resolver = provider.GetRequiredService<EntitlementResolver>();

        var set = await resolver.ResolveAsync(EntitlementSubject.ForGuild("gld_wiring_check"));

        // Not just "it did not throw" - self-host grants everything, so a resolver sitting on zero
        // sources (the AddEntitlementCache-without-AddEntitlements trap flagged in the task 11
        // report, where every key falls through to its catalogue default of false) would fail this
        // specific assertion even though GetRequiredService above would still have succeeded.
        Assert.That(set.Flag(EntitlementKeys.GuildPublicListing), Is.True,
            "self-host is supposed to grant everything - false here means the resolver answered " +
            "from an empty source list instead of SelfHostEverythingSource");
    }
}
