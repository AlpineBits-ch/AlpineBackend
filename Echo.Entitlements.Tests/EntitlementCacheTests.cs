using Echo.Entitlements.Caching;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Entitlements.Tests;

/// <summary>
/// The cache from spec section 4.3, and the three things it exists to get right: an event drops the
/// subject it names, a dropped event heals on the TTL, and a source that cannot answer fails open.
/// </summary>
[TestFixture]
public class EntitlementCacheTests
{
    private static readonly EntitlementSubject OtherGuild = EntitlementSubject.ForGuild("guild-2");

    [Test]
    public async Task A_second_resolution_of_the_same_subject_comes_from_the_cache()
    {
        var harness = Harness.WithGuildSlots(500);

        var first = await harness.Resolver.ResolveAsync(Subjects.Guild);
        var second = await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(first.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500));
            Assert.That(second.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500));
            Assert.That(harness.Source.Calls, Is.EqualTo(1),
                "the second resolution must not have gone back to the sources");
        });
    }

    [Test]
    public async Task Provenance_survives_the_round_trip()
    {
        var harness = Harness.WithGuildSlots(500, grantId: "grant-7");

        await harness.Resolver.ResolveAsync(Subjects.Guild);
        var cached = await harness.Resolver.ResolveAsync(Subjects.Guild);

        var provenance = cached.ProvenanceOf(EntitlementKeys.GuildEmojiSlots);

        Assert.Multiple(() =>
        {
            Assert.That(provenance.Source, Is.EqualTo(EntitlementPrecedence.AdminGrant));
            Assert.That(provenance.Detail, Is.EqualTo("grant-7"),
                "the admin provenance screen is built on this, so a cached answer that lost it would "
                + "make every billing ticket unanswerable");
        });
    }

    [Test]
    public async Task A_change_event_drops_the_subject_it_names()
    {
        var harness = Harness.WithGuildSlots(500);

        await harness.Resolver.ResolveAsync(Subjects.Guild);
        await harness.Invalidator.InvalidateAsync(Subjects.Guild);
        await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.That(harness.Source.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task A_change_event_drops_only_the_subject_it_names()
    {
        var harness = Harness.WithGuildSlots(500);

        await harness.Resolver.ResolveAsync(Subjects.Guild);
        await harness.Resolver.ResolveAsync(OtherGuild);
        await harness.Invalidator.InvalidateAsync(Subjects.Guild);

        await harness.Resolver.ResolveAsync(Subjects.Guild);
        await harness.Resolver.ResolveAsync(OtherGuild);

        Assert.That(harness.Source.Calls, Is.EqualTo(3),
            "two cold resolutions plus the one the invalidation forced; the untouched guild must still "
            + "be served from its cached entry");
    }

    [Test]
    public async Task Invalidating_a_subject_nobody_has_resolved_is_a_no_op()
    {
        var harness = Harness.WithGuildSlots(500);

        Assert.That(async () => await harness.Invalidator.InvalidateAsync(OtherGuild), Throws.Nothing);
        Assert.That(harness.Source.Calls, Is.Zero);
    }

    [Test]
    public async Task Repeating_the_same_change_event_changes_nothing_the_second_time()
    {
        var harness = Harness.WithGuildSlots(500);

        await harness.Resolver.ResolveAsync(Subjects.Guild);
        await harness.Invalidator.InvalidateAsync(Subjects.Guild);
        await harness.Invalidator.InvalidateAsync(Subjects.Guild);
        await harness.Resolver.ResolveAsync(Subjects.Guild);
        await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.That(harness.Source.Calls, Is.EqualTo(2),
            "the sweeper announces most expiries more than once, so a second copy of an event has to be "
            + "indistinguishable from the first");
    }

    [Test]
    public async Task A_dropped_event_still_heals_when_the_ttl_expires()
    {
        var harness = Harness.WithGuildSlots(500);

        await harness.Resolver.ResolveAsync(Subjects.Guild);

        // Nobody invalidates anything: this is the dropped-event case, which is the only reason the
        // TTL exists at all.
        harness.Clock.Advance(harness.Options.Ttl + TimeSpan.FromSeconds(1));
        await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.That(harness.Source.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task An_entry_is_still_served_one_tick_before_its_ttl()
    {
        var harness = Harness.WithGuildSlots(500);

        await harness.Resolver.ResolveAsync(Subjects.Guild);
        harness.Clock.Advance(harness.Options.Ttl - TimeSpan.FromSeconds(1));
        await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.That(harness.Source.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Billing_unreachable_serves_the_last_known_set()
    {
        var harness = Harness.WithGuildSlots(500);

        await harness.Resolver.ResolveAsync(Subjects.Guild);

        harness.Clock.Advance(harness.Options.Ttl + TimeSpan.FromSeconds(1));
        harness.Source.Fails = true;

        var resolved = await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.That(resolved.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500),
            "a billing outage that takes away what somebody paid for is the worse incident");
    }

    [Test]
    public void Billing_unreachable_does_not_throw()
    {
        var harness = Harness.WithGuildSlots(500);
        harness.Source.Fails = true;

        Assert.That(async () => await harness.Resolver.ResolveAsync(Subjects.Guild), Throws.Nothing);
    }

    [Test]
    public async Task A_subject_never_seen_before_falls_back_to_catalogue_defaults()
    {
        var harness = Harness.WithGuildSlots(500);
        harness.Source.Fails = true;

        var resolved = await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Value(EntitlementKeys.VoiceMaxParticipants).IsUnlimited, Is.True,
                "the catalogue floor is deliberately more generous than any plan, so an outage cannot "
                + "mute a voice channel");
            Assert.That(resolved.ProvenanceOf(EntitlementKeys.VoiceMaxParticipants).Source,
                Is.EqualTo(EntitlementPrecedence.CatalogueDefault));
        });
    }

    [Test]
    public async Task A_stale_answer_is_not_re_attempted_on_every_request()
    {
        var harness = Harness.WithGuildSlots(500);

        await harness.Resolver.ResolveAsync(Subjects.Guild);
        harness.Clock.Advance(harness.Options.Ttl + TimeSpan.FromSeconds(1));
        harness.Source.Fails = true;

        await harness.Resolver.ResolveAsync(Subjects.Guild);
        await harness.Resolver.ResolveAsync(Subjects.Guild);
        await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.That(harness.Source.Calls, Is.EqualTo(2),
            "one cold resolution and one failed retry; without the outage grace a dead Billing would be "
            + "asked once per request and the outage would spread");
    }

    [Test]
    public async Task A_recovery_is_picked_up_when_the_outage_grace_expires()
    {
        var harness = Harness.WithGuildSlots(500);

        await harness.Resolver.ResolveAsync(Subjects.Guild);
        harness.Clock.Advance(harness.Options.Ttl + TimeSpan.FromSeconds(1));
        harness.Source.Fails = true;
        await harness.Resolver.ResolveAsync(Subjects.Guild);

        harness.Source.Fails = false;
        harness.Clock.Advance(harness.Options.OutageGrace + TimeSpan.FromSeconds(1));
        await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.That(harness.Source.Calls, Is.EqualTo(3));
    }

    [Test]
    public async Task Catalogue_defaults_served_during_an_outage_do_not_become_the_last_known_set()
    {
        var harness = Harness.WithGuildSlots(500);
        harness.Source.Fails = true;

        await harness.Resolver.ResolveAsync(Subjects.Guild);

        // Past the grace the guess has expired entirely, so a later outage falls back to nothing
        // rather than to a number no source ever produced.
        harness.Clock.Advance(harness.Options.OutageGrace + TimeSpan.FromSeconds(1));

        Assert.That(harness.Store.Raw(harness.Keyspace.SetKey(Subjects.Guild)), Is.Not.Null,
            "the fake store expires lazily, so the entry is still there to be read");
        Assert.That(await harness.Store.ReadAsync(harness.Keyspace.SetKey(Subjects.Guild), default), Is.Null);
    }

    [Test]
    public async Task An_unreachable_cache_resolves_exactly_as_the_uncached_resolver_did()
    {
        var harness = Harness.WithGuildSlots(500);
        harness.Store.Broken = true;

        var first = await harness.Resolver.ResolveAsync(Subjects.Guild);
        var second = await harness.Resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(first.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500));
            Assert.That(second.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500));
            Assert.That(harness.Source.Calls, Is.EqualTo(2), "slower, never wrong");
        });
    }

    [Test]
    public async Task Concurrent_resolution_of_one_subject_asks_the_sources_once()
    {
        var harness = Harness.WithGuildSlots(500);
        var gate = new TaskCompletionSource();
        harness.Source.Gate = gate;

        // Started on this thread so every caller has joined the in-flight resolution before the gate
        // opens; a Task.Run per caller would make the assertion depend on scheduling.
        var callers = new List<Task<EntitlementSet>>();
        for (var i = 0; i < 32; i++) callers.Add(harness.Resolver.ResolveAsync(Subjects.Guild));

        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Source.Calls, Is.EqualTo(1),
                "a cold key under load must not stampede the backing store");
            Assert.That(results.Select(set => set.Number(EntitlementKeys.GuildEmojiSlots)),
                Is.All.EqualTo(500));
        });
    }

    [Test]
    public async Task The_cache_is_not_consulted_in_selfhost()
    {
        var clock = TestClock.AtEpoch();
        var store = new FakeEntitlementCacheStore(clock);
        var source = CountingSource.Returning(
            EntitlementPrecedence.AdminGrant, b => b.Number(EntitlementKeys.GuildEmojiSlots, 500));

        var resolver = Harness.Build(
            [new SelfHostEverythingSource(), source], store, clock, new EntitlementCacheOptions());

        var resolved = await resolver.ResolveAsync(Subjects.Guild);
        await resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(resolver.IsBypassed, Is.True);
            Assert.That(store.Reads, Is.Zero, "the license source answers from memory above the cache");
            Assert.That(store.Writes, Is.Zero);
            Assert.That(resolved.Value(EntitlementKeys.GuildEmojiSlots).IsUnlimited, Is.True);
        });
    }

    [Test]
    public async Task Both_sides_of_a_paired_key_are_cached()
    {
        var clock = TestClock.AtEpoch();
        var store = new FakeEntitlementCacheStore(clock);

        var source = new CountingSource(EntitlementPrecedence.Subscription, subject =>
        {
            var builder = new EntitlementSetBuilder(EntitlementPrecedence.Subscription);
            builder.Rung(EntitlementKeys.VoiceVideoCeiling,
                subject.Kind == SubjectKind.Guild ? "720p30" : "1080p60");
            return builder.Build();
        });

        var resolver = Harness.Build([source], store, clock, new EntitlementCacheOptions());

        var first = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);
        var second = await resolver.ResolveEffectiveAsync(Subjects.Guild, Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(first.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("720p30"),
                "the paired rule still takes the lower of the two through the cache");
            Assert.That(second.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("720p30"));
            Assert.That(source.Calls, Is.EqualTo(2),
                "one per subject, not one per pair - a cache keyed on the combination would be a key "
                + "per member per guild that no event invalidates");
        });
    }

    /// <summary>
    /// The shape this actually ships in: a grant source over a provider that reaches Billing, with
    /// the cache in front of it.
    /// </summary>
    [TestFixture]
    public class OverAGrantSource
    {
        [Test]
        public async Task A_grant_issued_after_the_event_is_picked_up_on_the_next_resolution()
        {
            var harness = GrantHarness.Build();

            var before = await harness.Resolver.ResolveAsync(Subjects.Guild);

            harness.Grants.Add(new EntitlementGrant(
                "grant-1", null, new Dictionary<string, string> { ["guild.emoji_slots"] = "500" }));
            await harness.Invalidator.InvalidateAsync(Subjects.Guild);

            var after = await harness.Resolver.ResolveAsync(Subjects.Guild);

            Assert.Multiple(() =>
            {
                Assert.That(before.Value(EntitlementKeys.GuildEmojiSlots).IsUnlimited, Is.True);
                Assert.That(after.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500));
                Assert.That(after.ProvenanceOf(EntitlementKeys.GuildEmojiSlots).Detail, Is.EqualTo("grant-1"));
            });
        }

        [Test]
        public async Task An_unreachable_billing_keeps_the_grant_rather_than_reading_as_no_grants()
        {
            var harness = GrantHarness.Build();
            harness.Grants.Add(new EntitlementGrant(
                "grant-1", null, new Dictionary<string, string> { ["guild.emoji_slots"] = "500" }));

            await harness.Resolver.ResolveAsync(Subjects.Guild);

            harness.Clock.Advance(TimeSpan.FromMinutes(5));
            harness.Provider.Fails = true;

            var resolved = await harness.Resolver.ResolveAsync(Subjects.Guild);

            Assert.That(resolved.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500),
                "a provider that swallowed the outage into an empty list would look like a successful "
                + "resolution with everything anybody paid for missing, and the cache would store it");
        }

        private sealed class GrantHarness
        {
            public required TestClock Clock { get; init; }

            public required ScriptedGrantProvider Provider { get; init; }

            public required List<EntitlementGrant> Grants { get; init; }

            public required CachedEntitlementResolver Resolver { get; init; }

            public required EntitlementCacheInvalidator Invalidator { get; init; }

            public static GrantHarness Build()
            {
                var clock = TestClock.AtEpoch();
                var store = new FakeEntitlementCacheStore(clock);
                var options = new EntitlementCacheOptions();
                var keyspace = new EntitlementCacheKeyspace(options.KeyPrefix, "testfing");
                var codec = new EntitlementSetCodec();
                var grants = new List<EntitlementGrant>();
                var provider = new ScriptedGrantProvider(grants);

                return new GrantHarness
                {
                    Clock = clock,
                    Provider = provider,
                    Grants = grants,
                    Resolver = new CachedEntitlementResolver(
                        [new GrantEntitlementSource(provider, PlanCatalogue.Empty, clock)],
                        store, keyspace, codec, options,
                        NullLogger<CachedEntitlementResolver>.Instance, clock),
                    Invalidator = new EntitlementCacheInvalidator(
                        store, keyspace, codec, options,
                        NullLogger<EntitlementCacheInvalidator>.Instance, clock),
                };
            }
        }

        private sealed class ScriptedGrantProvider(List<EntitlementGrant> grants) : IGrantProvider
        {
            public bool Fails { get; set; }

            public Task<IReadOnlyList<EntitlementGrant>> ActiveGrantsAsync(
                EntitlementSubject subject, CancellationToken cancellationToken) =>
                Fails
                    ? throw new InvalidOperationException("Billing is unreachable.")
                    : Task.FromResult<IReadOnlyList<EntitlementGrant>>(grants.ToList());
        }
    }

    private sealed class Harness
    {
        public required TestClock Clock { get; init; }

        public required FakeEntitlementCacheStore Store { get; init; }

        public required CountingSource Source { get; init; }

        public required CachedEntitlementResolver Resolver { get; init; }

        public required EntitlementCacheInvalidator Invalidator { get; init; }

        public required EntitlementCacheKeyspace Keyspace { get; init; }

        public required EntitlementCacheOptions Options { get; init; }

        public static Harness WithGuildSlots(long slots, string? grantId = null)
        {
            var clock = TestClock.AtEpoch();
            var store = new FakeEntitlementCacheStore(clock);
            var options = new EntitlementCacheOptions().Validate();
            var keyspace = new EntitlementCacheKeyspace(options.KeyPrefix, "testfing");
            var codec = new EntitlementSetCodec();

            var source = CountingSource.Returning(
                EntitlementPrecedence.AdminGrant,
                b => b.Number(EntitlementKeys.GuildEmojiSlots, slots, grantId));

            return new Harness
            {
                Clock = clock,
                Store = store,
                Source = source,
                Options = options,
                Keyspace = keyspace,
                Resolver = new CachedEntitlementResolver(
                    [source], store, keyspace, codec, options,
                    NullLogger<CachedEntitlementResolver>.Instance, clock),
                Invalidator = new EntitlementCacheInvalidator(
                    store, keyspace, codec, options,
                    NullLogger<EntitlementCacheInvalidator>.Instance, clock),
            };
        }

        public static CachedEntitlementResolver Build(
            IReadOnlyList<IEntitlementSource> sources,
            IEntitlementCacheStore store,
            TimeProvider clock,
            EntitlementCacheOptions options) =>
            new(sources, store,
                new EntitlementCacheKeyspace(options.KeyPrefix, "testfing"),
                new EntitlementSetCodec(), options,
                NullLogger<CachedEntitlementResolver>.Instance, clock);
    }
}

/// <summary>The payload, which is a contract with every other pod reading the same Redis and with
/// whichever build wrote the entry before this one was deployed.</summary>
[TestFixture]
public class EntitlementSetCodecTests
{
    [Test]
    public void A_set_round_trips_with_its_values_and_its_provenance()
    {
        var codec = new EntitlementSetCodec();
        var builder = new EntitlementSetBuilder(EntitlementPrecedence.AdminGrant);
        builder.Number(EntitlementKeys.GuildEmojiSlots, 500, "grant-1");
        builder.Flag(EntitlementKeys.GuildVanityUrl, true, "grant-1");
        builder.Rung(EntitlementKeys.VoiceVideoCeiling, "1080p30", "grant-2");
        builder.Number(EntitlementKeys.VoiceMaxParticipants, EntitlementValue.Unlimited, "grant-2");

        var fresh = DateTimeOffset.UtcNow.AddSeconds(60);
        var decoded = codec.Decode(codec.Encode(builder.Build(), fresh));

        Assert.That(decoded, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(decoded!.Value.Set.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500));
            Assert.That(decoded.Value.Set.Flag(EntitlementKeys.GuildVanityUrl), Is.True);
            Assert.That(decoded.Value.Set.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("1080p30"));
            Assert.That(decoded.Value.Set.Value(EntitlementKeys.VoiceMaxParticipants).IsUnlimited, Is.True);
            Assert.That(decoded.Value.Set.ProvenanceOf(EntitlementKeys.VoiceVideoCeiling).Detail,
                Is.EqualTo("grant-2"));
            Assert.That(decoded.Value.FreshUntil.ToUnixTimeMilliseconds(),
                Is.EqualTo(fresh.ToUnixTimeMilliseconds()));
        });
    }

    [Test]
    public void A_key_that_is_no_longer_in_the_catalogue_is_skipped_rather_than_thrown_on()
    {
        var payload =
            """
            {"f":32503680000000,"e":[{"k":"guild.emoji_slots","t":"Numeric","v":500,"s":"AdminGrant"},
            {"k":"guild.retired_key","t":"Numeric","v":1,"s":"AdminGrant"}]}
            """;

        var decoded = new EntitlementSetCodec().Decode(payload);

        Assert.That(decoded, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(decoded!.Value.Set.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(500),
                "one unreadable entry must not deny a subject everything else they hold");
            Assert.That(decoded.Value.Set.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void A_value_whose_kind_no_longer_matches_its_key_is_skipped()
    {
        var payload = """{"f":32503680000000,"e":[{"k":"guild.emoji_slots","t":"Flag","v":1,"s":"AdminGrant"}]}""";

        var decoded = new EntitlementSetCodec().Decode(payload);

        Assert.That(decoded!.Value.Set.Count, Is.Zero);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json at all")]
    [TestCase("{\"f\":1,")]
    public void An_unreadable_payload_is_a_miss_rather_than_a_failure(string payload)
    {
        Assert.That(new EntitlementSetCodec().Decode(payload), Is.Null);
    }

    [Test]
    public void A_corrupted_negative_value_is_skipped_rather_than_throwing_inside_a_cache_read()
    {
        var payload = """{"f":32503680000000,"e":[{"k":"guild.emoji_slots","t":"Numeric","v":-5,"s":"AdminGrant"}]}""";

        Assert.That(new EntitlementSetCodec().Decode(payload)!.Value.Set.Count, Is.Zero);
    }
}

/// <summary>Where an entry lives, and why two services must not silently share one.</summary>
[TestFixture]
public class EntitlementCacheKeyspaceTests
{
    [Test]
    public void Two_services_configured_the_same_way_share_a_cache_entry()
    {
        var left = EntitlementCacheKeyspace.FingerprintOf(
            [new SelfHostEverythingSource()], TierFixtures.Options());
        var right = EntitlementCacheKeyspace.FingerprintOf(
            [new SelfHostEverythingSource()], TierFixtures.Options());

        Assert.That(right, Is.EqualTo(left));
    }

    [Test]
    public void A_service_that_cannot_reach_billing_does_not_share_with_one_that_can()
    {
        var withGrants = EntitlementCacheKeyspace.FingerprintOf(
            [
                CountingSource.Returning(EntitlementPrecedence.AdminGrant, _ => { }),
                CountingSource.Returning(EntitlementPrecedence.PlanDefault, _ => { }),
            ],
            TierFixtures.Options());

        var withoutGrants = EntitlementCacheKeyspace.FingerprintOf(
            [CountingSource.Returning(EntitlementPrecedence.PlanDefault, _ => { })],
            TierFixtures.Options());

        Assert.That(withoutGrants, Is.Not.EqualTo(withGrants),
            "otherwise a service with no grant source would serve its narrower answer to one that has "
            + "grants, and a paying guild would read as a free one");
    }

    [Test]
    public void A_different_plan_table_is_a_different_keyspace()
    {
        var options = TierFixtures.Options();
        var edited = TierFixtures.Options();
        edited.Plans[TierFixtures.FreeGuild]["guild.emoji_slots"] = "51";

        Assert.That(
            EntitlementCacheKeyspace.FingerprintOf([], edited),
            Is.Not.EqualTo(EntitlementCacheKeyspace.FingerprintOf([], options)));
    }

    [Test]
    public void The_two_questions_about_one_subject_do_not_collide()
    {
        var keyspace = new EntitlementCacheKeyspace("entitlements", "abcd1234");

        Assert.Multiple(() =>
        {
            Assert.That(keyspace.SetKey(Subjects.Guild), Is.Not.EqualTo(keyspace.VersionKey(Subjects.Guild)));
            Assert.That(keyspace.SetKey(Subjects.Guild), Is.Not.EqualTo(keyspace.SetKey(Subjects.User)));
        });
    }
}

/// <summary>The three windows, checked against each other where a mistake is still a startup
/// failure.</summary>
[TestFixture]
public class EntitlementCacheOptionsTests
{
    [Test]
    public void The_shipped_ttl_is_no_longer_than_the_client_facing_one()
    {
        Assert.That(new EntitlementCacheOptions().ClientTtlSeconds, Is.LessThanOrEqualTo(60),
            "a client cache longer than the server's backstop defeats the self-healing one layer up");
    }

    [Test]
    public void A_retention_shorter_than_the_ttl_is_refused()
    {
        var options = new EntitlementCacheOptions { Retain = TimeSpan.FromSeconds(5) };

        Assert.That(options.Validate, Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void An_outage_grace_longer_than_the_ttl_is_refused()
    {
        var options = new EntitlementCacheOptions { OutageGrace = TimeSpan.FromMinutes(5) };

        Assert.That(options.Validate, Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void A_zero_ttl_is_refused_rather_than_silently_disabling_the_backstop()
    {
        var options = new EntitlementCacheOptions { Ttl = TimeSpan.Zero };

        Assert.That(options.Validate, Throws.InstanceOf<ArgumentOutOfRangeException>());
    }
}

/// <summary>The version counter, which fails open in the opposite direction to the set.</summary>
[TestFixture]
public class CachedEntitlementVersionProviderTests
{
    [Test]
    public async Task A_second_read_comes_from_the_cache()
    {
        var harness = VersionHarness.At(5);

        var first = await harness.Provider.VersionAsync(Subjects.Guild);
        var second = await harness.Provider.VersionAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(5));
            Assert.That(second, Is.EqualTo(5));
            Assert.That(harness.Inner.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task An_unreachable_counter_answers_the_last_number_it_knew()
    {
        var harness = VersionHarness.At(5);

        await harness.Provider.VersionAsync(Subjects.Guild);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        harness.Inner.Fails = true;

        Assert.That(await harness.Provider.VersionAsync(Subjects.Guild), Is.EqualTo(5),
            "answering zero to a client holding five makes it discard the snapshot it just asked for "
            + "and ask again, forever");
    }

    [Test]
    public async Task An_unreachable_counter_for_an_unknown_subject_answers_zero_without_throwing()
    {
        var harness = VersionHarness.At(5);
        harness.Inner.Fails = true;

        Assert.That(await harness.Provider.VersionAsync(Subjects.Guild), Is.Zero);
    }

    [Test]
    public async Task An_invalidation_drops_the_version_outright()
    {
        var harness = VersionHarness.At(5);

        await harness.Provider.VersionAsync(Subjects.Guild);
        harness.Inner.Version = 6;
        await harness.Invalidator.InvalidateAsync(Subjects.Guild);

        Assert.That(await harness.Provider.VersionAsync(Subjects.Guild), Is.EqualTo(6),
            "a stale version is worse than none: it tells a client that was pushed six to stop asking");
    }

    private sealed class VersionHarness
    {
        public required TestClock Clock { get; init; }

        public required ScriptedVersionProvider Inner { get; init; }

        public required CachedEntitlementVersionProvider Provider { get; init; }

        public required EntitlementCacheInvalidator Invalidator { get; init; }

        public static VersionHarness At(long version)
        {
            var clock = TestClock.AtEpoch();
            var store = new FakeEntitlementCacheStore(clock);
            var options = new EntitlementCacheOptions();
            var keyspace = new EntitlementCacheKeyspace(options.KeyPrefix, "testfing");
            var inner = new ScriptedVersionProvider(version);

            return new VersionHarness
            {
                Clock = clock,
                Inner = inner,
                Provider = new CachedEntitlementVersionProvider(
                    inner, store, keyspace, options,
                    NullLogger<CachedEntitlementVersionProvider>.Instance, clock),
                Invalidator = new EntitlementCacheInvalidator(
                    store, keyspace, new EntitlementSetCodec(), options,
                    NullLogger<EntitlementCacheInvalidator>.Instance, clock),
            };
        }
    }
}

/// <summary>The registration, because the cache is only useful if everything that already asks for a
/// resolver gets the cached one without knowing.</summary>
[TestFixture]
public class EntitlementCacheRegistrationTests
{
    [Test]
    public void The_registered_resolver_is_the_cached_one()
    {
        var provider = new ServiceCollection()
            .AddEntitlements()
            .AddEntitlementCache()
            .BuildServiceProvider();

        Assert.That(provider.GetRequiredService<EntitlementResolver>(),
            Is.InstanceOf<CachedEntitlementResolver>());
    }

    [Test]
    public void A_service_with_no_distributed_cache_still_starts_and_still_resolves()
    {
        var provider = new ServiceCollection()
            .AddEntitlements()
            .AddEntitlementCache()
            .BuildServiceProvider();

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetRequiredService<IEntitlementCacheStore>(),
                Is.InstanceOf<DisabledEntitlementCacheStore>());
            Assert.That(async () => await provider.GetRequiredService<EntitlementResolver>()
                .ResolveAsync(Subjects.Guild), Throws.Nothing);
        });
    }

    [Test]
    public void The_keyspace_is_fingerprinted_from_the_sources_the_service_actually_registered()
    {
        var withGrants = new ServiceCollection()
            .AddEntitlements()
            .AddSingleton<IEntitlementSource>(CountingSource.Returning(
                EntitlementPrecedence.AdminGrant, _ => { }))
            .AddEntitlementCache()
            .BuildServiceProvider();

        var withoutGrants = new ServiceCollection()
            .AddEntitlements()
            .AddEntitlementCache()
            .BuildServiceProvider();

        Assert.That(
            withoutGrants.GetRequiredService<EntitlementCacheKeyspace>().Fingerprint,
            Is.Not.EqualTo(withGrants.GetRequiredService<EntitlementCacheKeyspace>().Fingerprint));
    }

    [Test]
    public void The_version_cache_decorates_whatever_provider_was_registered()
    {
        var provider = new ServiceCollection()
            .AddEntitlements()
            .AddSingleton<IEntitlementVersionProvider>(new ScriptedVersionProvider(9))
            .AddEntitlementCache()
            .AddEntitlementVersionCache()
            .BuildServiceProvider();

        var versions = provider.GetRequiredService<IEntitlementVersionProvider>();

        Assert.That(versions, Is.InstanceOf<CachedEntitlementVersionProvider>());
        Assert.That(versions.VersionAsync(Subjects.Guild).AsTask().Result, Is.EqualTo(9));
    }

    [Test]
    public void Decorating_a_version_provider_nobody_registered_fails_loudly()
    {
        var services = new ServiceCollection().AddEntitlements().AddEntitlementCache();

        Assert.That(services.AddEntitlementVersionCache, Throws.InstanceOf<InvalidOperationException>());
    }
}
