using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.DependencyInjection;

namespace Echo.Entitlements.Tests;

[TestFixture]
public class DiscoveryEntitlementTests
{
    [Test]
    public void Both_discovery_keys_are_listed_in_the_catalogue()
    {
        Assert.That(EntitlementKeys.All, Does.Contain(EntitlementKeys.GuildPublicListing));
        Assert.That(EntitlementKeys.All, Does.Contain(EntitlementKeys.GuildRecruitment));
    }

    [Test]
    public void Free_withholds_both_and_plus_grants_both()
    {
        var free = Resolve(TierFixtures.FreeGuild);
        var plus = Resolve(TierFixtures.PlusGuild);

        Assert.Multiple(() =>
        {
            Assert.That(free.Flag(EntitlementKeys.GuildPublicListing), Is.False);
            Assert.That(free.Flag(EntitlementKeys.GuildRecruitment), Is.False);
            Assert.That(plus.Flag(EntitlementKeys.GuildPublicListing), Is.True);
            Assert.That(plus.Flag(EntitlementKeys.GuildRecruitment), Is.True);
        });
    }

    private static EntitlementSet Resolve(string plan)
    {
        var services = new ServiceCollection();
        services.AddEntitlements(options =>
        {
            options.DefaultGuildPlan = plan;
            options.Plans = TierFixtures.Options().Plans;
        });
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<EntitlementResolver>()
            .ResolveAsync(Subjects.Guild).GetAwaiter().GetResult();
    }
}
