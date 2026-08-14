using Billing.Application.Stripe;
using Billing.Domain.Aggregates;

namespace Billing.Tests;

/// <summary>The downgrade rule and the dunning clock, on their own.</summary>
[TestFixture]
public class SubscriptionDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    private static SubscriptionDecision Decide(
        SubscriptionStatus status,
        DateTimeOffset? gracePeriodEndsAt = null,
        StripeDunningSignal signal = StripeDunningSignal.None,
        DateTimeOffset? now = null) =>
        SubscriptionReconciler.Decide(status, gracePeriodEndsAt, signal, now ?? Now, Grace);

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public void An_active_subscription_keeps_the_plan_and_runs_no_clock()
    {
        var decision = Decide(SubscriptionStatus.Active);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Downgrade, Is.False);
            Assert.That(decision.GracePeriodEndsAt, Is.Null);
        });
    }

    [Test]
    public void A_trial_keeps_the_plan()
    {
        Assert.That(Decide(SubscriptionStatus.Trialing).Downgrade, Is.False);
    }

    /// <summary>
    /// The dunning rule: a failed payment must not take the tier away the same evening.
    /// </summary>
    [Test]
    public void A_failed_invoice_starts_the_clock_and_changes_nothing_else()
    {
        var decision = Decide(SubscriptionStatus.PastDue, signal: StripeDunningSignal.PaymentFailed);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Downgrade, Is.False);
            Assert.That(decision.GracePeriodEndsAt, Is.EqualTo(Now + Grace));
        });
    }

    [Test]
    public void A_paid_invoice_clears_the_clock()
    {
        var decision = Decide(
            SubscriptionStatus.Active,
            gracePeriodEndsAt: Now.AddDays(3),
            signal: StripeDunningSignal.PaymentSucceeded);

        Assert.Multiple(() =>
        {
            Assert.That(decision.GracePeriodEndsAt, Is.Null);
            Assert.That(decision.Downgrade, Is.False);
        });
    }

    [Test]
    public void The_tier_is_held_until_the_clock_runs_out_and_then_dropped()
    {
        var endsAt = Now + Grace;

        Assert.Multiple(() =>
        {
            Assert.That(Decide(SubscriptionStatus.PastDue, endsAt, now: Now.AddDays(6)).Downgrade, Is.False);
            Assert.That(Decide(SubscriptionStatus.PastDue, endsAt, now: endsAt).Downgrade, Is.True);
            Assert.That(Decide(SubscriptionStatus.PastDue, endsAt, now: endsAt.AddSeconds(1)).Downgrade, Is.True);
        });
    }

    [Test]
    public void A_cancelled_subscription_is_downgraded_immediately_whatever_the_clock_says()
    {
        // No grace period applies to a subscription that has ended.
        var decision = Decide(SubscriptionStatus.Canceled, gracePeriodEndsAt: Now.AddDays(6));

        Assert.That(decision.Downgrade, Is.True);
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    /// <summary>Stripe retries a failed invoice several times over a fortnight.</summary>
    [Test]
    public void A_second_failed_invoice_does_not_restart_the_clock()
    {
        var started = Now + Grace;

        var decision = Decide(
            SubscriptionStatus.PastDue,
            gracePeriodEndsAt: started,
            signal: StripeDunningSignal.PaymentFailed,
            now: Now.AddDays(3));

        Assert.That(decision.GracePeriodEndsAt, Is.EqualTo(started));
    }

    /// <summary>The safety net.</summary>
    [Test]
    public void A_past_due_subscription_with_no_clock_has_one_started()
    {
        var decision = Decide(SubscriptionStatus.PastDue);

        Assert.Multiple(() =>
        {
            Assert.That(decision.GracePeriodEndsAt, Is.EqualTo(Now + Grace));
            Assert.That(decision.Downgrade, Is.False);
        });
    }

    /// <summary>A failure notice can arrive while Stripe still reports the subscription as active.
    /// Clearing the clock then would throw away the grace the same delivery just started.</summary>
    [Test]
    public void A_failure_notice_arriving_against_a_still_active_subscription_keeps_its_clock()
    {
        var decision = Decide(SubscriptionStatus.Active, signal: StripeDunningSignal.PaymentFailed);

        Assert.That(decision.GracePeriodEndsAt, Is.EqualTo(Now + Grace));
    }

    /// <summary>Once Stripe says the subscription is in good standing again there is nothing
    /// outstanding for a clock to count down. Left running, an elapsed one would downgrade the
    /// subject the instant Stripe next flipped them to past_due, with none of the grace they are
    /// owed.</summary>
    [Test]
    public void A_recovered_subscription_stops_carrying_a_stale_clock()
    {
        var decision = Decide(SubscriptionStatus.Active, gracePeriodEndsAt: Now.AddDays(-1));

        Assert.Multiple(() =>
        {
            Assert.That(decision.GracePeriodEndsAt, Is.Null);
            Assert.That(decision.Downgrade, Is.False);
        });
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>A status Stripe adds next quarter.</summary>
    [Test]
    public void An_unknown_status_does_not_throw_and_does_not_read_as_live()
    {
        var parsed = SubscriptionStatuses.Parse("hibernating");

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.EqualTo(SubscriptionStatus.Unknown));
            Assert.That(SubscriptionStatuses.IsLive(parsed), Is.False);
            Assert.That(Decide(parsed).Downgrade, Is.True);
            Assert.That(Decide(SubscriptionStatuses.Parse(null)).Downgrade, Is.True);
        });
    }

    [Test]
    public void Every_status_outside_the_live_list_is_a_downgrade()
    {
        foreach (var status in Enum.GetValues<SubscriptionStatus>())
        {
            var expected = !SubscriptionStatuses.Live.Contains(status);

            Assert.That(Decide(status).Downgrade, Is.EqualTo(expected),
                $"{status} should {(expected ? "" : "not ")}be a downgrade");
        }
    }
}
