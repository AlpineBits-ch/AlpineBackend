using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;

namespace Echo.Entitlements.Tests;

/// <summary>Merging across sources, scope filtering, precedence and the fall to catalogue defaults.
/// The paired rule has its own fixture, since it is the one worth reading on its own.</summary>
[TestFixture]
public class EntitlementResolverTests
{
    [Test]
    public async Task Sources_merge_rather_than_overwrite_so_a_lower_one_cannot_take_anything_away()
    {
        var resolver = new EntitlementResolver(
        [
            StubSource.Returning(EntitlementPrecedence.AdminGrant,
                b => b.Number(EntitlementKeys.VoiceMaxParticipants, 25)),
            StubSource.Returning(EntitlementPrecedence.Subscription,
                b => b.Number(EntitlementKeys.VoiceMaxParticipants, 50)),
        ]);

        var resolved = await resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(50),
                "an admin grant of a smaller plan must never downgrade a paid subscription - that is "
                + "what 'sources are additive and never destructive' means");
            Assert.That(resolved.ProvenanceOf(EntitlementKeys.VoiceMaxParticipants).Source,
                Is.EqualTo(EntitlementPrecedence.Subscription),
                "the source credited is the one that actually supplied the winning value");
        });
    }

    [Test]
    public async Task Each_merge_rule_survives_the_trip_through_the_resolver()
    {
        var resolver = new EntitlementResolver(
        [
            StubSource.Returning(EntitlementPrecedence.AdminGrant, b => b
                .Flag(EntitlementKeys.GuildVanityUrl, false)
                .Number(EntitlementKeys.GuildEmojiSlots, 50)
                .Rung(EntitlementKeys.VoiceVideoCeiling, "480p30")),
            StubSource.Returning(EntitlementPrecedence.PlanDefault, b => b
                .Flag(EntitlementKeys.GuildVanityUrl, true)
                .Number(EntitlementKeys.GuildEmojiSlots, 200)
                .Rung(EntitlementKeys.VoiceVideoCeiling, "1080p30")),
        ]);

        var resolved = await resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Flag(EntitlementKeys.GuildVanityUrl), Is.True, "flags OR");
            Assert.That(resolved.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(200), "numerics max");
            Assert.That(resolved.Rung(EntitlementKeys.VoiceVideoCeiling), Is.EqualTo("1080p30"),
                "ladders take the highest rank");
        });
    }

    /// <summary>The full order from spec section 4.2.</summary>
    [Test]
    public async Task Precedence_credits_the_highest_standing_source_that_supplied_the_winning_value()
    {
        var order = new[]
        {
            EntitlementPrecedence.LicenseMode,
            EntitlementPrecedence.AdminGrant,
            EntitlementPrecedence.PromotionalGrant,
            EntitlementPrecedence.Subscription,
            EntitlementPrecedence.Boost,
            EntitlementPrecedence.PlanDefault,
        };

        for (var i = 0; i < order.Length; i++)
        {
            var present = order.Skip(i).ToArray();

            // Registered lowest first, so a resolver that trusted registration order rather than
            // precedence would credit the wrong one every time.
            var resolver = new EntitlementResolver(present
                .Reverse()
                .Select(p => StubSource.Returning(p, b => b.Number(EntitlementKeys.GuildEmojiSlots, 100)))
                .Cast<IEntitlementSource>()
                .ToList());

            var resolved = await resolver.ResolveAsync(Subjects.Guild);

            Assert.That(resolved.ProvenanceOf(EntitlementKeys.GuildEmojiSlots).Source,
                Is.EqualTo(present[0]),
                $"with {string.Join(", ", present)} all supplying the same value, {present[0]} outranks the rest");
        }
    }

    [Test]
    public async Task A_short_circuiting_source_stops_everything_below_it_being_consulted_at_all()
    {
        var billing = StubSource.Returning(EntitlementPrecedence.Subscription,
            b => b.Number(EntitlementKeys.GuildEmojiSlots, 500));

        var license = StubSource.Returning(EntitlementPrecedence.LicenseMode,
            b => b.Number(EntitlementKeys.GuildEmojiSlots, EntitlementValue.Unlimited),
            shortCircuits: true);

        var resolved = await new EntitlementResolver([billing, license]).ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Value(EntitlementKeys.GuildEmojiSlots).IsUnlimited, Is.True);
            Assert.That(billing.Asked, Is.Empty,
                "in selfhost the Billing service is not deployed, so the win is not calling it rather "
                + "than ignoring what it said");
        });
    }

    [Test]
    public async Task A_source_that_does_not_short_circuit_leaves_the_rest_of_the_order_running()
    {
        var lower = StubSource.Returning(EntitlementPrecedence.PlanDefault,
            b => b.Number(EntitlementKeys.GuildEmojiSlots, 50));

        var upper = StubSource.Returning(EntitlementPrecedence.AdminGrant,
            b => b.Number(EntitlementKeys.GuildAuditLogDays, 90));

        var resolved = await new EntitlementResolver([upper, lower]).ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(lower.Asked, Has.Count.EqualTo(1));
            Assert.That(resolved.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(50));
            Assert.That(resolved.Number(EntitlementKeys.GuildAuditLogDays), Is.EqualTo(90));
        });
    }

    [Test]
    public async Task A_guild_scoped_key_is_not_taken_from_a_user_subject()
    {
        var overreaching = StubSource.Returning(EntitlementPrecedence.Subscription,
            b => b.Number(EntitlementKeys.VoiceMaxParticipants, 50));

        var resolver = new EntitlementResolver([overreaching]);

        var asUser = await resolver.ResolveAsync(Subjects.User);
        var asGuild = await resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(asUser.Contains(EntitlementKeys.VoiceMaxParticipants), Is.False,
                "a guild key resolved against a user must not exist at all, or an enforcement site "
                + "would read a room size off the person rather than the guild");
            Assert.That(asGuild.Number(EntitlementKeys.VoiceMaxParticipants), Is.EqualTo(50));
        });
    }

    [Test]
    public async Task A_user_scoped_key_is_not_taken_from_a_guild_subject()
    {
        var overreaching = StubSource.Returning(EntitlementPrecedence.Subscription,
            b => b.Number(EntitlementKeys.UserMaxDevices, 10));

        var resolver = new EntitlementResolver([overreaching]);

        var asGuild = await resolver.ResolveAsync(Subjects.Guild);
        var asUser = await resolver.ResolveAsync(Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(asGuild.Contains(EntitlementKeys.UserMaxDevices), Is.False);
            Assert.That(asUser.Number(EntitlementKeys.UserMaxDevices), Is.EqualTo(10));
        });
    }

    [Test]
    public async Task A_resolved_set_carries_exactly_the_keys_that_apply_to_the_subject()
    {
        var resolver = new EntitlementResolver([]);

        var guild = await resolver.ResolveAsync(Subjects.Guild);
        var user = await resolver.ResolveAsync(Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(guild.Entries.Select(e => e.Key),
                Is.EquivalentTo(EntitlementKeys.For(SubjectKind.Guild)));
            Assert.That(user.Entries.Select(e => e.Key),
                Is.EquivalentTo(EntitlementKeys.For(SubjectKind.User)));
        });
    }

    [Test]
    public async Task A_key_no_source_supplies_falls_to_its_catalogue_default()
    {
        var partial = StubSource.Returning(EntitlementPrecedence.Subscription,
            b => b.Number(EntitlementKeys.GuildEmojiSlots, 500));

        var resolved = await new EntitlementResolver([partial]).ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Value(EntitlementKeys.GuildAuditLogDays).Raw,
                Is.EqualTo(EntitlementKeys.GuildAuditLogDays.Default.Raw));
            Assert.That(resolved.ProvenanceOf(EntitlementKeys.GuildAuditLogDays).Source,
                Is.EqualTo(EntitlementPrecedence.CatalogueDefault),
                "the provenance screen has to be able to say 'nobody', not leave a blank");
        });
    }

    [Test]
    public async Task With_no_sources_at_all_every_key_is_its_default()
    {
        var resolved = await new EntitlementResolver([]).ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            foreach (var entry in resolved.Entries)
            {
                Assert.That(entry.Value.Raw, Is.EqualTo(entry.Key.Default.Raw));
                Assert.That(entry.Provenance.Source, Is.EqualTo(EntitlementPrecedence.CatalogueDefault));
            }
        });
    }

    [Test]
    public async Task A_source_that_answers_nothing_is_not_an_error()
    {
        var silent = new StubSource(EntitlementPrecedence.AdminGrant, _ => EntitlementSet.Empty);

        var resolved = await new EntitlementResolver([silent]).ResolveAsync(Subjects.Guild);

        Assert.That(resolved.Number(EntitlementKeys.GuildEmojiSlots),
            Is.EqualTo(EntitlementKeys.GuildEmojiSlots.Default.AsNumber));
    }

    [Test]
    public void A_source_cannot_claim_the_catalogue_default_as_its_own_provenance()
    {
        Assert.That(() => new EntitlementSetBuilder(EntitlementPrecedence.CatalogueDefault),
            Throws.InstanceOf<ArgumentException>(),
            "it is not a source, and a source claiming it would make the provenance screen lie");
    }

    [Test]
    public void A_source_cannot_set_a_key_to_a_value_of_the_wrong_shape()
    {
        var builder = new EntitlementSetBuilder(EntitlementPrecedence.AdminGrant);

        Assert.That(() => builder.Set(EntitlementKeys.GuildEmojiSlots, EntitlementValue.OfFlag(true)),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void One_source_setting_the_same_key_twice_keeps_the_more_generous_value()
    {
        var set = new EntitlementSetBuilder(EntitlementPrecedence.AdminGrant)
            .Number(EntitlementKeys.GuildEmojiSlots, 200, "grant-a")
            .Number(EntitlementKeys.GuildEmojiSlots, 50, "grant-b")
            .Build();

        Assert.That(set.Number(EntitlementKeys.GuildEmojiSlots), Is.EqualTo(200),
            "two overlapping grants in one band follow the same rule as two bands");
    }

    [Test]
    public void An_empty_subject_id_is_refused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => EntitlementSubject.ForGuild(""), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => EntitlementSubject.ForUser("  "), Throws.InstanceOf<ArgumentException>());
        });
    }
}
