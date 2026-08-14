using Echo.Entitlements.Caching;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.DependencyInjection;

namespace Echo.Entitlements.Tests;

/// <summary>
/// The catalogue as a source rather than as a value, which is what a plan table that is edited
/// while the process runs requires.
/// </summary>
[TestFixture]
public class PlanCatalogueSourceTests
{
    // ── The revision ─────────────────────────────────────────────────────────

    [Test]
    public async Task A_fixed_source_answers_its_catalogue_and_one_revision()
    {
        var source = new FixedPlanCatalogueSource(TierFixtures.Catalogue());

        var first = await source.CurrentAsync();
        source.Invalidate();
        var second = await source.CurrentAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.SameAs(second),
                "there is nothing behind a configured catalogue to go back to");
            Assert.That(source.Revision, Is.EqualTo(PlanCatalogueRevision.Of(first)));
        });
    }

    /// <summary>Two processes that read the same plans have to agree, or every service would key its
    /// cache entries apart and resolve everything for itself. So the revision is over the contents,
    /// and not over when they were read or how they were ordered.</summary>
    [Test]
    public void Two_catalogues_with_the_same_plans_have_the_same_revision()
    {
        var first = new PlanCatalogue([Plan("pro", 50), Plan("free", 10)]);
        var second = new PlanCatalogue([Plan("free", 10), Plan("pro", 50)]);

        Assert.That(PlanCatalogueRevision.Of(first), Is.EqualTo(PlanCatalogueRevision.Of(second)));
    }

    [Test]
    public void A_moved_number_moves_the_revision()
    {
        var before = new PlanCatalogue([Plan("pro", 50)]);
        var after = new PlanCatalogue([Plan("pro", 75)]);
        var renamed = new PlanCatalogue([Plan("pro", 50, "Professional")]);
        var added = new PlanCatalogue([Plan("pro", 50), Plan("plus", 25)]);

        Assert.Multiple(() =>
        {
            Assert.That(PlanCatalogueRevision.Of(after), Is.Not.EqualTo(PlanCatalogueRevision.Of(before)));
            Assert.That(PlanCatalogueRevision.Of(added), Is.Not.EqualTo(PlanCatalogueRevision.Of(before)));
            Assert.That(PlanCatalogueRevision.Of(renamed), Is.Not.EqualTo(PlanCatalogueRevision.Of(before)),
                "a display name is on the wire and on a settings screen, so it is part of the answer");
        });
    }

    [Test]
    public void An_empty_catalogue_has_a_revision_of_its_own()
    {
        Assert.That(PlanCatalogueRevision.Of(PlanCatalogue.Empty),
            Is.Not.EqualTo(PlanCatalogueRevision.Of(new PlanCatalogue([Plan("pro", 50)]))));
    }

    // ── What resolves through it ─────────────────────────────────────────────

    [Test]
    public async Task The_plan_source_resolves_through_whatever_the_catalogue_says_now()
    {
        var catalogue = new MutableCatalogueSource(new PlanCatalogue([Plan("pro", 50)]));
        var source = new PlanDefaultEntitlementSource(
            catalogue, new ScriptedPlanAssignment("pro", null));

        var before = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        catalogue.Publish(new PlanCatalogue([Plan("pro", 75)]));
        var after = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(before.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(50));
            Assert.That(after.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(75),
                "an edited plan has to change what an enforcing service resolves, or the console is "
                + "the only thing the edit reached");
        });
    }

    /// <summary>The negative case, and the state almost every subject on an instance with no
    /// configured default is in: no plan applies, every key falls to its catalogue default, and the
    /// catalogue is not even consulted.</summary>
    [Test]
    public async Task A_subject_on_no_plan_contributes_nothing()
    {
        var catalogue = new MutableCatalogueSource(new PlanCatalogue([Plan("pro", 50)]));
        var source = new PlanDefaultEntitlementSource(
            catalogue, new ScriptedPlanAssignment(null, null));

        var resolved = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Entries, Is.Empty);
            Assert.That(catalogue.Reads, Is.Zero,
                "there is nothing to look up, and a catalogue read is a call to Billing");
        });
    }

    /// <summary>A plan name the catalogue cannot resolve contributes nothing rather than throwing.
    /// It is the state during a rolling deploy, and the same absence the snapshot reports by leaving
    /// the plan object off entirely.</summary>
    [Test]
    public async Task A_plan_the_catalogue_does_not_know_contributes_nothing()
    {
        var source = new PlanDefaultEntitlementSource(
            new FixedPlanCatalogueSource(new PlanCatalogue([Plan("pro", 50)])),
            new ScriptedPlanAssignment("ghost", null));

        var resolved = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.That(resolved.Entries, Is.Empty);
    }

    // ── The cache key ────────────────────────────────────────────────────────

    /// <summary>The invalidation that reaches subjects nobody announced.</summary>
    [Test]
    public void A_changed_catalogue_rolls_the_set_key_and_leaves_the_version_key_alone()
    {
        var catalogue = new MutableCatalogueSource(new PlanCatalogue([Plan("pro", 50)]));
        var keyspace = new EntitlementCacheKeyspace("entitlements", "abcd1234", () => catalogue.Revision);

        var beforeSet = keyspace.SetKey(Subjects.Guild);
        var beforeVersion = keyspace.VersionKey(Subjects.Guild);

        catalogue.Publish(new PlanCatalogue([Plan("pro", 75)]));

        Assert.Multiple(() =>
        {
            Assert.That(keyspace.SetKey(Subjects.Guild), Is.Not.EqualTo(beforeSet));
            Assert.That(keyspace.VersionKey(Subjects.Guild), Is.EqualTo(beforeVersion),
                "the entitlement version is Billing's counter and says nothing about what a plan "
                + "contains; rolling it would throw away the client's staleness check for nothing");
        });
    }

    /// <summary>Two services reading the same catalogue share their entries, which is the property
    /// worth having: one Billing round trip warms all of them.</summary>
    [Test]
    public void Two_services_on_the_same_catalogue_share_a_key()
    {
        var one = new EntitlementCacheKeyspace(
            "entitlements", "abcd1234", () => PlanCatalogueRevision.Of(new PlanCatalogue([Plan("pro", 50)])));
        var other = new EntitlementCacheKeyspace(
            "entitlements", "abcd1234", () => PlanCatalogueRevision.Of(new PlanCatalogue([Plan("pro", 50)])));

        Assert.That(one.SetKey(Subjects.Guild), Is.EqualTo(other.SetKey(Subjects.Guild)));
    }

    // ── Registration ─────────────────────────────────────────────────────────

    /// <summary>What a service gets with nothing but <c>AddEntitlements</c>: its own configuration, as
    /// a source, plus the concrete fallbacks a Billing-backed registration replaces the interfaces
    /// with and then leans on.</summary>
    [Test]
    public async Task Registering_entitlements_registers_the_configured_catalogue_as_a_source()
    {
        var provider = new ServiceCollection()
            .AddEntitlements(options =>
            {
                options.DefaultGuildPlan = TierFixtures.FreeGuild;
                options.DefaultUserPlan = TierFixtures.FreeUser;
                options.Plans = TierFixtures.Options().Plans;
            })
            .AddEntitlementCache()
            .BuildServiceProvider();

        var catalogue = provider.GetRequiredService<IPlanCatalogueSource>();
        var keyspace = provider.GetRequiredService<EntitlementCacheKeyspace>();
        var fallback = provider.GetRequiredService<FixedPlanAssignment>();

        Assert.Multiple(async () =>
        {
            Assert.That(catalogue, Is.InstanceOf<FixedPlanCatalogueSource>());
            Assert.That((await catalogue.CurrentAsync()).Find(TierFixtures.ProGuild), Is.Not.Null);
            Assert.That(fallback.DefaultFor(SubjectKind.Guild), Is.EqualTo(TierFixtures.FreeGuild));
            Assert.That(fallback.DefaultFor(SubjectKind.User), Is.EqualTo(TierFixtures.FreeUser));
            Assert.That(keyspace.CatalogueRevision, Is.EqualTo(catalogue.Revision),
                "the cache keyspace has to be reading the registered source, or a plan change rolls "
                + "nothing");
        });
    }

    /// <summary>An unassigned subject resolves to the instance's default, which is the state almost
    /// every subject is in: the free tier is what a subject is on, not a state somebody put them in.
    /// </summary>
    [Test]
    public async Task An_unassigned_subject_resolves_to_the_configured_default_plan()
    {
        var provider = new ServiceCollection()
            .AddEntitlements(options =>
            {
                options.DefaultGuildPlan = TierFixtures.FreeGuild;
                options.Plans = TierFixtures.Options().Plans;
            })
            .BuildServiceProvider();

        var resolved = await provider.GetRequiredService<EntitlementResolver>().ResolveAsync(Subjects.Guild);

        Assert.That(resolved.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(50));
    }

    /// <summary>And the rule that stays intact: a default nobody configured is not invented. An
    /// instance with plans but no default resolves every key to its catalogue default, which is what
    /// the client renders as no plan row at all.</summary>
    [Test]
    public async Task An_instance_with_no_configured_default_puts_nobody_on_a_plan()
    {
        var provider = new ServiceCollection()
            .AddEntitlements(options => options.Plans = TierFixtures.Options().Plans)
            .BuildServiceProvider();

        var resolved = await provider.GetRequiredService<EntitlementResolver>().ResolveAsync(Subjects.Guild);

        Assert.That(resolved.Value(EntitlementKeys.VoiceMaxParticipants).IsUnlimited, Is.True);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static PlanDefinition Plan(string name, long participants, string? displayName = null) =>
        new(
            name,
            new Dictionary<EntitlementKey, EntitlementValue>
            {
                [EntitlementKeys.VoiceMaxParticipants] = EntitlementValue.OfNumber(participants),
            },
            displayName);

    /// <summary>A catalogue that changes underneath a running process, which is what Billing's table
    /// is. Counts its reads so a test can show the source did not consult it when there was nothing to
    /// look up.</summary>
    private sealed class MutableCatalogueSource(PlanCatalogue initial) : IPlanCatalogueSource
    {
        private PlanCatalogue _catalogue = initial;

        public int Reads { get; private set; }

        public string Revision { get; private set; } = PlanCatalogueRevision.Of(initial);

        public ValueTask<PlanCatalogue> CurrentAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(_catalogue);
        }

        public void Invalidate()
        {
        }

        public void Publish(PlanCatalogue catalogue)
        {
            _catalogue = catalogue;
            Revision = PlanCatalogueRevision.Of(catalogue);
        }
    }
}
