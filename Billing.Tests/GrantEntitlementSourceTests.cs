using Billing.Tests.Helpers;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;

namespace Billing.Tests;

/// <summary><see cref="GrantEntitlementSource"/> on its own and inside the resolver.</summary>
[TestFixture]
public class GrantEntitlementSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static GrantEntitlementSource Source(
        TestClock clock,
        EntitlementPrecedence band = EntitlementPrecedence.AdminGrant,
        params EntitlementGrant[] grants) =>
        new(new StubGrantProvider(grants), Plans.Catalogue(), clock, band);

    [Test]
    public async Task A_plan_grant_contributes_that_plans_values_at_the_admin_grant_band()
    {
        var source = Source(new TestClock(Now), EntitlementPrecedence.AdminGrant,
            new EntitlementGrant("gran_1", Plans.Pro, null));

        var set = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(set.Number(GrantFixtures.Participants), Is.EqualTo(50));
            Assert.That(set.Flag(GrantFixtures.VanityUrl), Is.True);
            Assert.That(set.ProvenanceOf(GrantFixtures.Participants).Source,
                Is.EqualTo(EntitlementPrecedence.AdminGrant));
            Assert.That(set.ProvenanceOf(GrantFixtures.Participants).Detail, Is.EqualTo("gran_1"),
                "The grant id is what the provenance screen sends an operator to look up.");
        });
    }

    [Test]
    public async Task An_entitlements_grant_contributes_only_the_keys_it_names()
    {
        var source = Source(new TestClock(Now), EntitlementPrecedence.AdminGrant,
            new EntitlementGrant("gran_1", null, new Dictionary<string, string>
            {
                ["guild.emoji_slots"] = "250",
            }));

        var set = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(set.Number(GrantFixtures.Emoji), Is.EqualTo(250));
            Assert.That(set.Contains(GrantFixtures.Participants), Is.False);
        });
    }

    /// <summary>Null expiry is permanent, and nothing about the passage of time changes that. A clock
    /// far past anything a campaign would use still leaves the grant contributing.</summary>
    [Test]
    public async Task A_permanent_grant_never_expires()
    {
        var clock = new TestClock(Now);
        var source = Source(clock, EntitlementPrecedence.AdminGrant,
            new EntitlementGrant("gran_1", Plans.Pro, null, ExpiresAt: null));

        clock.Advance(TimeSpan.FromDays(365 * 20));
        var set = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.That(set.Number(GrantFixtures.Participants), Is.EqualTo(50));
    }

    /// <summary>The provider is expected to have filtered already, so this is the second line: a
    /// cached provider answer does not expire on its own, and a subject holding a lapsed grant
    /// forever is the one failure here that nothing downstream would notice.</summary>
    [Test]
    public async Task An_expired_grant_contributes_nothing_even_if_the_provider_still_returns_it()
    {
        var clock = new TestClock(Now);
        var source = Source(clock, EntitlementPrecedence.AdminGrant,
            new EntitlementGrant("gran_1", Plans.Pro, null, Now.AddDays(1)));

        var before = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(2));
        var after = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(before.Number(GrantFixtures.Participants), Is.EqualTo(50));
            Assert.That(after.Count, Is.Zero);
        });
    }

    [Test]
    public async Task Two_overlapping_grants_keep_the_more_generous_value_and_credit_the_grant_that_won()
    {
        var source = Source(new TestClock(Now), EntitlementPrecedence.AdminGrant,
            new EntitlementGrant("gran_plus", Plans.Plus, null),
            new EntitlementGrant("gran_pro", Plans.Pro, null));

        var set = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(set.Number(GrantFixtures.Participants), Is.EqualTo(50));
            Assert.That(set.ProvenanceOf(GrantFixtures.Participants).Detail, Is.EqualTo("gran_pro"),
                "Crediting the row that was read last rather than the one that supplied the number "
                + "would make the provenance screen point an operator at the wrong grant.");
        });
    }

    /// <summary>Validation belongs at issue time.</summary>
    [Test]
    public async Task An_unknown_key_or_an_unparseable_value_is_skipped_rather_than_thrown_on()
    {
        var source = Source(new TestClock(Now), EntitlementPrecedence.AdminGrant,
            new EntitlementGrant("gran_1", null, new Dictionary<string, string>
            {
                ["guild.emoji_slots"] = "250",
                ["guild.teleportation"] = "true",
                ["voice.max_participants"] = "as many as you like",
                ["voice.video_ceiling"] = "4320p120",
            }));

        var set = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(set.Number(GrantFixtures.Emoji), Is.EqualTo(250));
            Assert.That(set.Contains(GrantFixtures.Participants), Is.False);
            Assert.That(set.Contains(GrantFixtures.VideoCeiling), Is.False);
        });
    }

    [Test]
    public async Task A_plan_the_instance_has_not_configured_contributes_nothing()
    {
        var source = Source(new TestClock(Now), EntitlementPrecedence.AdminGrant,
            new EntitlementGrant("gran_1", "enterprise", null));

        var set = await source.ResolveAsync(Subjects.Guild, CancellationToken.None);

        Assert.That(set.Count, Is.Zero);
    }

    /// <summary>Campaign-scoped and credit-funded grants sit one band lower (spec section 4.2). One
    /// instance cannot speak for two bands, so the band is chosen at construction and anything that is
    /// not a grant band is refused.</summary>
    [Test]
    public void A_source_cannot_claim_a_band_that_is_not_a_grant_band()
    {
        var promotional = Source(new TestClock(Now), EntitlementPrecedence.PromotionalGrant,
            new EntitlementGrant("gran_1", Plans.Pro, null));

        Assert.Multiple(() =>
        {
            Assert.That(promotional.Precedence, Is.EqualTo(EntitlementPrecedence.PromotionalGrant));

            Assert.That(
                () => Source(new TestClock(Now), EntitlementPrecedence.Subscription),
                Throws.ArgumentException);

            Assert.That(
                () => Source(new TestClock(Now), EntitlementPrecedence.LicenseMode),
                Throws.ArgumentException);
        });
    }

    /// <summary>
    /// The reading of spec section 4.2 settled after WP-01: precedence orders and attributes, the
    /// merge rule decides the value.
    /// </summary>
    [Test]
    public async Task A_grant_below_the_subscription_cannot_lower_it()
    {
        var plans = Plans.Catalogue();
        var subscription = new StubSubscriptionSource(plans.Find(Plans.Pro)!);

        var resolver = new EntitlementResolver(
        [
            new GrantEntitlementSource(
                new StubGrantProvider(new EntitlementGrant("gran_plus", Plans.Plus, null)),
                plans,
                new TestClock(Now)),
            subscription,
        ]);

        var set = await resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(set.Number(GrantFixtures.Participants), Is.EqualTo(50),
                "Pro's 50 must survive a Plus grant laid over it.");
            Assert.That(set.Flag(GrantFixtures.VanityUrl), Is.True,
                "A flag the subscription granted cannot be turned off by a grant that does not have it.");
            Assert.That(set.ProvenanceOf(GrantFixtures.Participants).Source,
                Is.EqualTo(EntitlementPrecedence.Subscription),
                "The subscription supplied the winning value, so it is what the provenance screen credits.");
        });
    }

    /// <summary>The direction that is the point of the feature: a grant above a subscription raises
    /// the guild, and the credit moves to the grant because the grant is what supplied the number.
    /// </summary>
    [Test]
    public async Task A_grant_above_the_subscription_raises_it_and_takes_the_credit()
    {
        var plans = Plans.Catalogue();

        var resolver = new EntitlementResolver(
        [
            new GrantEntitlementSource(
                new StubGrantProvider(new EntitlementGrant("gran_pro", Plans.Pro, null)),
                plans,
                new TestClock(Now)),
            new StubSubscriptionSource(plans.Find(Plans.Free)!),
        ]);

        var set = await resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(set.Number(GrantFixtures.Participants), Is.EqualTo(50));
            Assert.That(set.ProvenanceOf(GrantFixtures.Participants).Source,
                Is.EqualTo(EntitlementPrecedence.AdminGrant));
            Assert.That(set.ProvenanceOf(GrantFixtures.Participants).Detail, Is.EqualTo("gran_pro"));
        });
    }

    /// <summary>The single most important property in this package.</summary>
    [Test]
    public async Task A_grant_over_a_subscription_expiring_leaves_the_subscription_working()
    {
        var plans = Plans.Catalogue();
        var clock = new TestClock(Now);
        var subscription = new StubSubscriptionSource(plans.Find(Plans.Plus)!);

        var resolver = new EntitlementResolver(
        [
            new GrantEntitlementSource(
                new StubGrantProvider(new EntitlementGrant("gran_pro", Plans.Pro, null, Now.AddDays(90))),
                plans,
                clock),
            subscription,
        ]);

        var during = await resolver.ResolveAsync(Subjects.Guild);

        clock.Advance(TimeSpan.FromDays(91));
        var after = await resolver.ResolveAsync(Subjects.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(during.Number(GrantFixtures.Participants), Is.EqualTo(50),
                "The grant raises the guild to Pro while it lasts.");
            Assert.That(during.Rung(GrantFixtures.VideoCeiling), Is.EqualTo("1080p60"));

            Assert.That(after.Number(GrantFixtures.Participants), Is.EqualTo(25),
                "Once the grant lapses the guild is back on exactly what it pays for - not below it.");
            Assert.That(after.Rung(GrantFixtures.VideoCeiling), Is.EqualTo("1080p30"));
            Assert.That(after.Number(GrantFixtures.Emoji), Is.EqualTo(200));
            Assert.That(after.ProvenanceOf(GrantFixtures.Participants).Source,
                Is.EqualTo(EntitlementPrecedence.Subscription));

            Assert.That(subscription.Calls, Is.EqualTo(2),
                "The subscription source is consulted on every resolution. A grant that short-circuited "
                + "it would be the mechanism by which an expiry could lose the paid state.");
        });
    }

    /// <summary>Scope is enforced by the resolver, once, rather than by trusting every source. A grant
    /// naming a guild key on a user subject is dropped instead of widening the key's scope.</summary>
    [Test]
    public async Task A_guild_key_granted_to_a_user_is_dropped_by_the_resolver()
    {
        var resolver = new EntitlementResolver(
        [
            new GrantEntitlementSource(
                new StubGrantProvider(new EntitlementGrant("gran_1", null, new Dictionary<string, string>
                {
                    ["guild.emoji_slots"] = "500",
                    ["user.max_devices"] = "20",
                })),
                Plans.Catalogue(),
                new TestClock(Now)),
        ]);

        var set = await resolver.ResolveAsync(Subjects.User);

        Assert.Multiple(() =>
        {
            Assert.That(set.Number(EntitlementKeys.UserMaxDevices), Is.EqualTo(20));
            Assert.That(set.ProvenanceOf(GrantFixtures.Emoji).Source,
                Is.EqualTo(EntitlementPrecedence.CatalogueDefault));
        });
    }
}
