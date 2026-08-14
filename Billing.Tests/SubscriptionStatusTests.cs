using Billing.Domain.Aggregates;

namespace Billing.Tests;

/// <summary>
/// The Stripe status vocabulary, and the one property that keeps a new Stripe status from taking
/// the service down.
/// </summary>
[TestFixture]
public class SubscriptionStatusTests
{
    [TestCase("incomplete", SubscriptionStatus.Incomplete)]
    [TestCase("incomplete_expired", SubscriptionStatus.IncompleteExpired)]
    [TestCase("trialing", SubscriptionStatus.Trialing)]
    [TestCase("active", SubscriptionStatus.Active)]
    [TestCase("past_due", SubscriptionStatus.PastDue)]
    [TestCase("canceled", SubscriptionStatus.Canceled)]
    [TestCase("unpaid", SubscriptionStatus.Unpaid)]
    [TestCase("paused", SubscriptionStatus.Paused)]
    public void Every_status_Stripe_sends_today_maps_to_its_member(string wire, SubscriptionStatus expected) =>
        Assert.That(SubscriptionStatuses.Parse(wire), Is.EqualTo(expected));

    [TestCase("  active  ")]
    [TestCase("ACTIVE")]
    public void Whitespace_and_case_do_not_change_the_answer(string wire) =>
        Assert.That(SubscriptionStatuses.Parse(wire), Is.EqualTo(SubscriptionStatus.Active));

    /// <summary>The one that decides the design.</summary>
    [TestCase("some_status_stripe_added_next_quarter")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void An_unrecognised_status_is_Unknown_rather_than_a_throw(string? wire)
    {
        SubscriptionStatus parsed = default;

        Assert.Multiple(() =>
        {
            Assert.That(() => parsed = SubscriptionStatuses.Parse(wire), Throws.Nothing);
            Assert.That(parsed, Is.EqualTo(SubscriptionStatus.Unknown));
        });
    }

    [Test]
    public void Unknown_is_not_live()
    {
        // The safe direction.
        Assert.That(SubscriptionStatuses.IsLive(SubscriptionStatus.Unknown), Is.False);
    }

    [TestCase(SubscriptionStatus.Trialing, true)]
    [TestCase(SubscriptionStatus.Active, true)]
    [TestCase(SubscriptionStatus.PastDue, true)]
    [TestCase(SubscriptionStatus.Incomplete, false)]
    [TestCase(SubscriptionStatus.IncompleteExpired, false)]
    [TestCase(SubscriptionStatus.Canceled, false)]
    [TestCase(SubscriptionStatus.Unpaid, false)]
    [TestCase(SubscriptionStatus.Paused, false)]
    public void The_live_set_is_exactly_trialing_active_and_past_due(SubscriptionStatus status, bool live)
    {
        var subscription = new Subscription { Status = status };

        Assert.Multiple(() =>
        {
            Assert.That(SubscriptionStatuses.IsLive(status), Is.EqualTo(live));
            Assert.That(subscription.IsLive, Is.EqualTo(live));
        });
    }

    /// <summary>
    /// <c>PastDue</c> being live is a deliberate commercial decision, not an oversight: a failed
    /// payment must not take the tier away the same evening, and the dunning grace is what eventually
    /// ends it. Asserted by name so that removing it has to be done on purpose.
    /// </summary>
    [Test]
    public void Past_due_keeps_the_subject_on_their_plan()
    {
        Assert.That(SubscriptionStatuses.Live, Does.Contain(SubscriptionStatus.PastDue));
    }

    /// <summary>The member names, verbatim, because they are what
    /// <c>HasConversion&lt;string&gt;()</c> writes into the column and what the filtered unique index
    /// on <c>subscriptions</c> compares against in raw SQL.</summary>
    [Test]
    public void The_stored_spelling_of_the_live_statuses_matches_the_index_filter()
    {
        var stored = SubscriptionStatuses.Live.Select(status => status.ToString()).ToArray();

        Assert.That(stored, Is.EquivalentTo(new[] { "Trialing", "Active", "PastDue" }));
    }

    [Test]
    public void Mirroring_copies_what_Stripe_says_and_leaves_the_grace_period_alone()
    {
        var periodEnd = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
        var eventAt = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        var subscription = new Subscription
        {
            Status = SubscriptionStatus.Active,
            GracePeriodEndsAt = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
        };

        subscription.MirrorFromStripe(
            SubscriptionStatus.PastDue, periodEnd, cancelAtPeriodEnd: true, "in_123", eventAt);

        Assert.Multiple(() =>
        {
            Assert.That(subscription.Status, Is.EqualTo(SubscriptionStatus.PastDue));
            Assert.That(subscription.CurrentPeriodEnd, Is.EqualTo(periodEnd));
            Assert.That(subscription.CancelAtPeriodEnd, Is.True);
            Assert.That(subscription.LatestInvoiceId, Is.EqualTo("in_123"));
            Assert.That(subscription.LastEventAt, Is.EqualTo(eventAt));

            // Dunning is a policy decision that needs a clock and a configured grace, so it belongs
            // to the reconciler rather than to the record. Mirroring must not quietly clear it.
            Assert.That(subscription.GracePeriodEndsAt,
                Is.EqualTo(new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)));
        });
    }
}
