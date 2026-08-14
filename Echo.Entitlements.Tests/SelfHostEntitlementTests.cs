using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Echo.Entitlements.Tests;

/// <summary>
/// Self-hosting: everything at maximum, nothing below consulted, and no configuration required to
/// get there.
/// </summary>
[TestFixture]
public class SelfHostEntitlementTests
{
    private static EntitlementValue Maximum(EntitlementKey key) => SelfHostEverythingSource.Maximum(key);

    /// <summary>A source below the license band that fails the test if it is consulted at all. The
    /// short circuit is not an optimisation - in selfhost the Billing service is not deployed, so a
    /// call here is a call to something that does not exist.</summary>
    private static StubSource NeverConsulted() =>
        new(EntitlementPrecedence.PlanDefault, _ =>
            throw new InvalidOperationException(
                "the self-host source short-circuits, so no lower source may be consulted"));

    [Test]
    public async Task Self_host_yields_the_maximum_for_every_key_in_the_catalogue()
    {
        var lower = NeverConsulted();
        var resolver = new EntitlementResolver([new SelfHostEverythingSource(), lower]);

        var guild = await resolver.ResolveAsync(Subjects.Guild);
        var user = await resolver.ResolveAsync(Subjects.User);

        Assert.Multiple(() =>
        {
            foreach (var key in EntitlementKeys.All)
            {
                var set = key.AppliesTo(SubjectKind.Guild) ? guild : user;

                Assert.That(set.Contains(key), Is.True,
                    $"'{key.Name}' is in the catalogue but self-hosting resolved nothing for it");
                Assert.That(set.Value(key).Raw, Is.EqualTo(Maximum(key).Raw),
                    $"'{key.Name}' is not at its maximum on a self-hosted instance");
                Assert.That(set.ProvenanceOf(key).Source, Is.EqualTo(EntitlementPrecedence.LicenseMode),
                    $"'{key.Name}' was credited to something other than the license mode");

                // A paired key is set by both sides, so the other subject must be at maximum too or
                // the effective min would bind on the missing half.
                if (key.Scope == EntitlementScope.Paired)
                {
                    Assert.That(user.Value(key).Raw, Is.EqualTo(Maximum(key).Raw));
                    Assert.That(guild.Value(key).Raw, Is.EqualTo(Maximum(key).Raw));
                }
            }
        });
    }

    /// <summary>The specific key a "return the defaults" implementation withholds.</summary>
    [Test]
    public async Task Self_host_grants_the_vanity_url_flag_that_the_catalogue_default_withholds()
    {
        var resolver = new EntitlementResolver([new SelfHostEverythingSource()]);

        var guild = await resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(EntitlementKeys.GuildVanityUrl.Default.AsFlag, Is.False,
                "if this default is ever flipped on, this test stops proving anything");
            Assert.That(guild.Flag(EntitlementKeys.GuildVanityUrl), Is.True);
        });
    }

    /// <summary>The stronger form of the claim above: the lower source is not merely ignored, it is
    /// never asked. <see cref="StubSource.Asked"/> records every call.</summary>
    [Test]
    public async Task Self_host_never_consults_a_lower_source()
    {
        var subscription = StubSource.Returning(
            EntitlementPrecedence.Subscription,
            builder => builder.Number(EntitlementKeys.VoiceMaxParticipants, 10));

        var resolver = new EntitlementResolver([subscription, new SelfHostEverythingSource()]);

        await resolver.ResolveAsync(Subjects.Guild);
        await resolver.ResolveAsync(Subjects.User);

        Assert.That(subscription.Asked, Is.Empty,
            "the Billing service is not deployed in selfhost, so a source below the license band "
            + "must not be called at all");
    }

    [Test]
    public async Task Hosted_registers_no_license_source_and_the_lower_sources_decide()
    {
        var subscription = StubSource.Returning(
            EntitlementPrecedence.Subscription,
            builder => builder.Number(EntitlementKeys.VoiceMaxParticipants, 25));

        var provider = new ServiceCollection()
            .AddSingleton<IEntitlementSource>(subscription)
            .AddEntitlements(options =>
            {
                options.DefaultGuildPlan = TierFixtures.FreeGuild;
                options.Plans = TierFixtures.Options().Plans;
            })
            .AddLicenseMode(LicenseMode.Hosted)
            .BuildServiceProvider();

        var guild = await provider.GetRequiredService<EntitlementResolver>().ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetServices<IEntitlementSource>().OfType<SelfHostEverythingSource>(), Is.Empty,
                "hosted is an explicit opt-in: there is no state in which a hosted instance carries "
                + "a short-circuiting self-host source");
            Assert.That(subscription.Asked, Is.Not.Empty, "hosted must consult the sources below it");
            Assert.That(guild.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(25));
            Assert.That(guild.Flag(EntitlementKeys.GuildVanityUrl), Is.False,
                "nothing granted the vanity flag here, so hosted must not hand it out");
        });
    }

    [Test]
    public void Self_host_mode_is_the_default_and_an_unknown_mode_fails_loudly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LicenseModes.Parse(null), Is.EqualTo(LicenseMode.SelfHost));
            Assert.That(LicenseModes.Parse("  "), Is.EqualTo(LicenseMode.SelfHost));
            Assert.That(LicenseModes.Parse(" SelfHost "), Is.EqualTo(LicenseMode.SelfHost));
            Assert.That(LicenseModes.Parse("hosted"), Is.EqualTo(LicenseMode.Hosted));
            Assert.That(() => LicenseModes.Parse("hostd"), Throws.InstanceOf<ArgumentException>(),
                "falling back to selfhost on a typo would silently give the hosted instance away");
        });
    }

    [Test]
    public void Registering_self_host_mode_puts_the_source_at_the_top_of_the_order()
    {
        var provider = new ServiceCollection()
            .AddEntitlements()
            .AddLicenseMode(LicenseMode.SelfHost)
            .BuildServiceProvider();

        var sources = provider.GetServices<IEntitlementSource>().ToList();
        var licenseSource = sources.OfType<SelfHostEverythingSource>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(licenseSource.Precedence, Is.EqualTo(EntitlementPrecedence.LicenseMode));
            Assert.That(licenseSource.ShortCircuits, Is.True);
            Assert.That(sources.Min(source => source.Precedence), Is.EqualTo(EntitlementPrecedence.LicenseMode));
        });
    }
}

/// <summary>
/// Operator ceilings: what this box will do, as opposed to what a guild is allowed.
/// </summary>
[TestFixture]
public class OperatorCeilingTests
{
    private static EntitlementSet SelfHosted()
    {
        var builder = new EntitlementSetBuilder(EntitlementPrecedence.LicenseMode);
        foreach (var key in EntitlementKeys.All) builder.Set(key, SelfHostEverythingSource.Maximum(key));
        return builder.Build();
    }

    [Test]
    public void A_ceiling_below_the_entitlement_wins()
    {
        var ceilings = OperatorCeilings.Parse(new Dictionary<string, string?>
        {
            ["voice.max_participants"] = "12",
            ["voice.video_ceiling"] = "720p30",
            ["storage.upload_max_bytes"] = "26214400",
        });

        var resolved = SelfHosted();

        Assert.Multiple(() =>
        {
            Assert.That(ceilings.Effective(resolved, EntitlementKeys.VoiceMaxParticipants).AsNumber,
                Is.EqualTo(12), "self-hosting resolved unlimited, but this box carries 12");
            Assert.That(EntitlementKeys.VoiceVideoCeiling.Format(
                    ceilings.Effective(resolved, EntitlementKeys.VoiceVideoCeiling)),
                Is.EqualTo("720p30"));
            Assert.That(ceilings.Effective(resolved, EntitlementKeys.StorageUploadMaxBytes).AsNumber,
                Is.EqualTo(26214400));
            Assert.That(ceilings.Binds(EntitlementKeys.VoiceMaxParticipants, resolved.Value(EntitlementKeys.VoiceMaxParticipants)),
                Is.True, "the enforcement site needs to know to report an operator ceiling rather "
                + "than a plan limit somebody could pay to lift");
        });
    }

    [Test]
    public void A_ceiling_above_the_entitlement_does_not_raise_it()
    {
        var plan = new EntitlementSetBuilder(EntitlementPrecedence.PlanDefault)
            .Number(EntitlementKeys.VoiceMaxParticipants, 10)
            .Rung(EntitlementKeys.VoiceVideoCeiling, "720p30")
            .Build();

        var ceilings = OperatorCeilings.Parse(new Dictionary<string, string?>
        {
            ["voice.max_participants"] = "500",
            ["voice.video_ceiling"] = "1080p60",
        });

        Assert.Multiple(() =>
        {
            Assert.That(ceilings.Effective(plan, EntitlementKeys.VoiceMaxParticipants).AsNumber, Is.EqualTo(10),
                "a generous env var is not a grant, or an operator could buy Pro with a text editor");
            Assert.That(EntitlementKeys.VoiceVideoCeiling.Format(
                    ceilings.Effective(plan, EntitlementKeys.VoiceVideoCeiling)),
                Is.EqualTo("720p30"));
            Assert.That(ceilings.Binds(EntitlementKeys.VoiceMaxParticipants, plan.Value(EntitlementKeys.VoiceMaxParticipants)),
                Is.False);
        });
    }

    [Test]
    public void An_unset_ceiling_leaves_the_entitlement_exactly_as_resolved()
    {
        var resolved = SelfHosted();

        Assert.Multiple(() =>
        {
            Assert.That(OperatorCeilings.None.IsEmpty, Is.True);

            foreach (var key in EntitlementKeys.All)
            {
                Assert.That(OperatorCeilings.None.Effective(resolved, key).Raw,
                    Is.EqualTo(resolved.Value(key).Raw), $"'{key.Name}' was clamped by nothing at all");
            }

            // Blank is how an operator who has not set the variable arrives here, and it must mean
            // the same as absent rather than parsing as a zero ceiling that closes the box.
            var blank = OperatorCeilings.Parse(new Dictionary<string, string?>
            {
                ["voice.max_participants"] = "",
                ["voice.video_ceiling"] = null,
                ["storage.upload_max_bytes"] = "   ",
            });

            Assert.That(blank.IsEmpty, Is.True);
        });
    }

    [Test]
    public void A_ceiling_on_an_unknown_or_malformed_key_fails_at_startup()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => OperatorCeilings.Parse(new Dictionary<string, string?>
                {
                    ["voice.max_particpants"] = "12",
                }),
                Throws.InstanceOf<KeyNotFoundException>(),
                "a typo would otherwise be a ceiling that silently never applies");

            Assert.That(() => OperatorCeilings.Parse(new Dictionary<string, string?>
                {
                    ["voice.video_ceiling"] = "4k120",
                }),
                Throws.InstanceOf<ArgumentException>());

            Assert.That(() => OperatorCeilings.Parse(new Dictionary<string, string?>
                {
                    ["voice.max_participants"] = "lots",
                }),
                Throws.InstanceOf<FormatException>());
        });
    }

    [Test]
    public void A_ceiling_of_the_wrong_shape_for_its_key_is_refused()
    {
        Assert.That(() => new OperatorCeilings(new Dictionary<EntitlementKey, EntitlementValue>
            {
                [EntitlementKeys.VoiceMaxParticipants] = EntitlementValue.OfFlag(true),
            }),
            Throws.InstanceOf<ArgumentException>());
    }
}
