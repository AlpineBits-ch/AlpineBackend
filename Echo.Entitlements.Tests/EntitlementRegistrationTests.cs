using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Echo.Entitlements.Tests;

/// <summary>The registration a consuming service makes.</summary>
[TestFixture]
public class EntitlementRegistrationTests
{
    [Test]
    public async Task Registering_with_no_sources_resolves_everything_to_todays_behaviour()
    {
        var provider = new ServiceCollection().AddEntitlements().BuildServiceProvider();

        var resolved = await provider.GetRequiredService<EntitlementResolver>().ResolveAsync(Subjects.Guild);

        Assert.That(resolved.Value(EntitlementKeys.VoiceMaxParticipants).IsUnlimited, Is.True,
            "a service that adds entitlements before any plan exists must not start enforcing "
            + "limits nobody agreed to");
    }

    [Test]
    public async Task A_source_registered_by_the_consuming_service_is_picked_up()
    {
        var services = new ServiceCollection();
        services.AddEntitlements(options =>
        {
            options.DefaultGuildPlan = TierFixtures.FreeGuild;
            options.Plans = TierFixtures.Options().Plans;
        });
        services.AddSingleton<IEntitlementSource>(StubSource.Returning(
            EntitlementPrecedence.AdminGrant, b => b.Number(EntitlementKeys.GuildEmojiSlots, 500, "grant-1")));

        var provider = services.BuildServiceProvider();
        var resolved = await provider.GetRequiredService<EntitlementResolver>().ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500));
            Assert.That(resolved.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(10),
                "the plan source registered by AddEntitlements is still the floor underneath it");
        });
    }

    [Test]
    public async Task Plans_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Entitlements:DefaultGuildPlan"] = "starter",
                ["Entitlements:Plans:starter:voice.max_participants"] = "12",
                ["Entitlements:Plans:starter:voice.video_ceiling"] = "480p30",
                ["Entitlements:Plans:starter:guild.vanity_url"] = "true",
            })
            .Build();

        var provider = new ServiceCollection().AddEntitlements(configuration).BuildServiceProvider();
        var resolved = await provider.GetRequiredService<EntitlementResolver>().ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(12));
            Assert.That(resolved.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("480p30"));
            Assert.That(resolved.Flag(EntitlementKeys.GuildVanityUrl), Is.True);
        });
    }

    [Test]
    public void A_misconfigured_plan_fails_at_registration_rather_than_at_an_enforcement_site()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Entitlements:Plans:starter:voice.max_particpants"] = "12",
            })
            .Build();

        Assert.That(() => new ServiceCollection().AddEntitlements(configuration),
            Throws.InstanceOf<KeyNotFoundException>());
    }

    [Test]
    public void The_resolver_is_a_singleton_because_it_holds_no_per_request_state()
    {
        var provider = new ServiceCollection().AddEntitlements().BuildServiceProvider();

        var first = provider.GetRequiredService<EntitlementResolver>();
        var second = provider.GetRequiredService<EntitlementResolver>();

        Assert.That(second, Is.SameAs(first));
    }
}
