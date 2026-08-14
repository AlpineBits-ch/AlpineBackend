using AppEnvironment;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Billing.Tests;

/// <summary>The webhook end to end, from a signed body to the plan assignment behind it.</summary>
[TestFixture]
public class StripeWebhookReconciliationTests
{
    private const string SubscriptionId = StripeWebhookFixtures.SubscriptionId;
    private const string CustomerId = "cus_billingtests";
    private const string ProPriceId = "price_pro_v1";
    private const string GuildId = "gild_01JQZZZZZZZZZZZZZZZZZZZZZZ";
    private const string PayerId = "user_payer";

    private static readonly DateTimeOffset Start = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly EntitlementSubject Subject = new(SubjectKind.Guild, GuildId);

    private string _originalWebhookSecret = string.Empty;
    private string _originalSecretKey = string.Empty;

    private MicroserviceContext _db = null!;
    private IStripeGateway _gateway = null!;
    private TestClock _clock = null!;
    private SubscriptionReconciler _reconciler = null!;
    private StripeWebhookProcessor _processor = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        _originalWebhookSecret = Env.License.StripeWebhookSecret;
        _originalSecretKey = Env.License.StripeSecretKey;

        Env.License.StripeWebhookSecret = StripeWebhookFixtures.WebhookSecret;
        Env.License.StripeSecretKey = "sk_test_billingtests";

        await PostgresTestDatabase.ResetToEmptyAsync();
        await BuildAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        Env.License.StripeWebhookSecret = _originalWebhookSecret;
        Env.License.StripeSecretKey = _originalSecretKey;

        await _db.DisposeAsync();
    }

    // ── Normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task An_activation_puts_the_subject_on_the_plan_the_price_names()
    {
        Live(SubscriptionStatus.Active);

        var response = await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_activate", StripeEventTypes.SubscriptionCreated));

        var assignment = await AssignmentAsync();
        var subscription = await _db.Subscriptions.SingleAsync();
        var pro = await PlanIdAsync("pro");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(200));
            Assert.That(assignment!.PlanId, Is.EqualTo(pro));
            Assert.That(assignment.VersionNumber, Is.EqualTo(1));
            Assert.That(assignment.AssignedBy, Is.EqualTo($"stripe:{SubscriptionId}"));
            Assert.That(assignment.Reason, Does.Contain(StripeEventTypes.SubscriptionCreated));

            Assert.That(subscription.Status, Is.EqualTo(SubscriptionStatus.Active));
            Assert.That(subscription.PayerUserId, Is.EqualTo(PayerId));
            Assert.That(subscription.SubjectId, Is.EqualTo(GuildId));

            Assert.That(response.Announcements, Has.Count.EqualTo(1));
            Assert.That(response.Announcements[0].Version, Is.EqualTo(1));
        });
    }

    /// <summary>The test the whole design exists for.</summary>
    [Test]
    public async Task Two_events_delivered_out_of_order_converge_on_the_same_state()
    {
        // The subscription is genuinely active - the customer paid.
        var stale = StripeWebhookFixtures.SubscriptionEvent(
            "evt_stale", payloadStatus: "canceled", created: 1_760_000_000);

        var fresh = StripeWebhookFixtures.SubscriptionEvent(
            "evt_fresh", payloadStatus: "past_due", created: 1_760_009_999);

        var forwards = await ReplayAsync(SubscriptionStatus.Active, fresh, stale);
        var backwards = await ReplayAsync(SubscriptionStatus.Active, stale, fresh);

        // And the mirror image, so the test cannot pass by never downgrading anybody.
        var endedForwards = await ReplayAsync(SubscriptionStatus.Canceled, fresh, stale);
        var endedBackwards = await ReplayAsync(SubscriptionStatus.Canceled, stale, fresh);

        Assert.Multiple(() =>
        {
            Assert.That(forwards, Is.EqualTo("pro"));
            Assert.That(backwards, Is.EqualTo("pro"));
            Assert.That(endedForwards, Is.EqualTo("free"));
            Assert.That(endedBackwards, Is.EqualTo("free"));
        });
    }

    [Test]
    public async Task A_failed_invoice_holds_the_tier_for_the_grace_period_and_the_sweeper_drops_it_after()
    {
        Live(SubscriptionStatus.Active);
        await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_seed", StripeEventTypes.SubscriptionCreated));

        Live(SubscriptionStatus.PastDue);
        await DeliverAsync(StripeWebhookFixtures.InvoiceEvent(
            "evt_failed", StripeEventTypes.InvoicePaymentFailed));

        var held = await AssignmentAsync();
        var duringGrace = await _db.Subscriptions.SingleAsync();
        var heldPlan = await PlanNameAsync(held!.PlanId);

        Assert.Multiple(() =>
        {
            Assert.That(heldPlan, Is.EqualTo("pro"),
                "a failed payment must not take the tier away the same evening");
            Assert.That(duringGrace.GracePeriodEndsAt, Is.EqualTo(Start.AddDays(7)));
            Assert.That(duringGrace.IsLive, Is.True);
        });

        // Nothing arrives from Stripe at the moment a grace period ends, which is the whole reason
        // the sweep exists.
        _clock.Advance(TimeSpan.FromDays(8));

        var announcements = await DunningSweeper.CollectAsync(
            _db, _reconciler, _clock.GetUtcNow(), CancellationToken.None);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var dropped = await AssignmentAsync();
        var droppedPlan = await PlanNameAsync(dropped!.PlanId);

        Assert.Multiple(() =>
        {
            Assert.That(announcements, Has.Count.EqualTo(1));
            Assert.That(droppedPlan, Is.EqualTo("free"));
            Assert.That(dropped.Reason, Does.Contain("grace"));
        });
    }

    [Test]
    public async Task An_invoice_paid_clears_an_existing_grace_period()
    {
        Live(SubscriptionStatus.Active);
        await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_seed", StripeEventTypes.SubscriptionCreated));

        Live(SubscriptionStatus.PastDue);
        await DeliverAsync(StripeWebhookFixtures.InvoiceEvent(
            "evt_failed", StripeEventTypes.InvoicePaymentFailed));

        Assert.That((await _db.Subscriptions.SingleAsync()).GracePeriodEndsAt, Is.Not.Null);

        Live(SubscriptionStatus.Active);
        await DeliverAsync(StripeWebhookFixtures.InvoiceEvent("evt_paid", StripeEventTypes.InvoicePaid));

        var recovered = await _db.Subscriptions.SingleAsync();
        var stillOn = await PlanNameAsync((await AssignmentAsync())!.PlanId);

        Assert.Multiple(() =>
        {
            Assert.That(recovered.GracePeriodEndsAt, Is.Null);
            Assert.That(stillOn, Is.EqualTo("pro"));
        });
    }

    /// <summary>The end of a paid relationship writes the default plan explicitly.</summary>
    [Test]
    public async Task The_end_of_a_subscription_assigns_the_default_plan_rather_than_deleting_the_row()
    {
        Live(SubscriptionStatus.Active);
        await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_seed", StripeEventTypes.SubscriptionCreated));

        // Deleted at Stripe since. Null from the gateway is a real answer rather than a failure.
        _gateway.GetSubscriptionAsync(SubscriptionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StripeSubscriptionSnapshot?>(null));

        await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_deleted", StripeEventTypes.SubscriptionDeleted));

        var assignment = await AssignmentAsync();
        Assert.That(assignment, Is.Not.Null, "the row is the provenance; deleting it loses the story");

        var plan = await PlanNameAsync(assignment!.PlanId);
        var subscription = await _db.Subscriptions.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(plan, Is.EqualTo("free"));
            Assert.That(assignment.AssignedBy, Is.EqualTo($"stripe:{SubscriptionId}"));
            Assert.That(assignment.Reason, Does.Contain("no longer exists"));
            Assert.That(subscription.Status, Is.EqualTo(SubscriptionStatus.Canceled));
        });
    }

    // ── Edge ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task The_same_event_delivered_twice_changes_nothing_the_second_time()
    {
        Live(SubscriptionStatus.Active);

        var payload = StripeWebhookFixtures.SubscriptionEvent(
            "evt_once", StripeEventTypes.SubscriptionCreated);

        var first = await DeliverAsync(payload);

        // The plan moves underneath, so a second application would be visible rather than silent.
        Live(SubscriptionStatus.Canceled);

        var second = await DeliverAsync(payload);

        var assignment = await AssignmentAsync();
        var recorded = await _db.ProcessedStripeEvents.CountAsync();
        var plan = await PlanNameAsync(assignment!.PlanId);

        Assert.Multiple(() =>
        {
            Assert.That(first.Announcements, Has.Count.EqualTo(1));
            Assert.That(second.StatusCode, Is.EqualTo(200));
            Assert.That(second.Announcements, Is.Empty);
            Assert.That(second.Body, Is.EqualTo("Already handled."));
            Assert.That(recorded, Is.EqualTo(1));
            Assert.That(plan, Is.EqualTo("pro"));
        });

        // The duplicate did not even ask Stripe, which is what makes a redelivery storm cheap.
        await _gateway.Received(1).GetSubscriptionAsync(SubscriptionId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A checkout that never completes must not cost somebody a plan they already had.
    /// </summary>
    [Test]
    public async Task A_subscription_that_never_went_live_leaves_somebody_elses_assignment_alone()
    {
        var pro = await PlanIdAsync("pro");

        _db.PlanAssignments.Add(new PlanAssignment
        {
            Id = PlanAssignment.GenerateId(),
            SubjectKind = Subject.Kind,
            SubjectId = Subject.Id,
            PlanId = pro,
            VersionNumber = 1,
            AssignedBy = "user_admin",
            Reason = "Launch partner, for the first year.",
            AssignedAt = Start.AddDays(-30),
        });

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Live(SubscriptionStatus.Incomplete);

        var response = await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_incomplete", StripeEventTypes.SubscriptionCreated));

        var assignment = await AssignmentAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(200));
            Assert.That(response.Announcements, Is.Empty);
            Assert.That(assignment!.PlanId, Is.EqualTo(pro));
            Assert.That(assignment.AssignedBy, Is.EqualTo("user_admin"));
            Assert.That(assignment.Reason, Is.EqualTo("Launch partner, for the first year."));
        });
    }

    /// <summary>The upgrade race, which is the same guard seen from the other side.</summary>
    [Test]
    public async Task A_cancellation_does_not_undo_the_assignment_a_newer_subscription_made()
    {
        Live(SubscriptionStatus.Active);
        await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_old_seed", StripeEventTypes.SubscriptionCreated));

        // The replacement subscription has already been reconciled, so the assignment is attributed
        // to it rather than to the one that is now being cancelled.
        var assignment = await _db.PlanAssignments.SingleAsync();
        assignment.AssignedBy = "stripe:sub_thereplacement";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        _gateway.GetSubscriptionAsync(SubscriptionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StripeSubscriptionSnapshot?>(null));

        var response = await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_old_deleted", StripeEventTypes.SubscriptionDeleted));

        var after = await AssignmentAsync();
        var plan = await PlanNameAsync(after!.PlanId);

        Assert.Multiple(() =>
        {
            Assert.That(plan, Is.EqualTo("pro"));
            Assert.That(after.AssignedBy, Is.EqualTo("stripe:sub_thereplacement"));
            Assert.That(response.Announcements, Is.Empty);
        });
    }

    /// <summary>A delivery that changes nothing is most of them.</summary>
    [Test]
    public async Task A_delivery_that_moves_nobody_announces_nothing()
    {
        Live(SubscriptionStatus.Active);

        await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_first", StripeEventTypes.SubscriptionCreated));

        var second = await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_second", StripeEventTypes.SubscriptionUpdated));

        Assert.That(second.Announcements, Is.Empty);
    }

    /// <summary>A dispute is recorded and alerted on, and downgrades nobody.</summary>
    [Test]
    public async Task A_dispute_does_not_downgrade()
    {
        Live(SubscriptionStatus.Active);
        await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_seed", StripeEventTypes.SubscriptionCreated));

        var response = await DeliverAsync(StripeWebhookFixtures.DisputeEvent("evt_dispute"));

        var assignment = await AssignmentAsync();
        var recorded = await _db.ProcessedStripeEvents.SingleAsync(row => row.EventId == "evt_dispute");
        var plan = await PlanNameAsync(assignment!.PlanId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(200));
            Assert.That(response.Announcements, Is.Empty);
            Assert.That(plan, Is.EqualTo("pro"));
            Assert.That(recorded.Outcome, Does.Contain("Dispute"));
            Assert.That(recorded.ProcessedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task An_event_type_this_service_does_not_act_on_is_recorded_and_ignored()
    {
        var response = await DeliverAsync(StripeWebhookFixtures.UnrelatedEvent("evt_unrelated"));

        var recorded = await _db.ProcessedStripeEvents.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(200));
            Assert.That(recorded.Outcome, Does.Contain("not an event this service acts on"));
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    /// <summary>An unrecognised status must not throw - a throw fails the delivery, Stripe retries,
    /// and the retry fails identically until somebody deploys. Not live is recoverable and loud.
    /// </summary>
    [Test]
    public async Task An_unknown_stripe_status_is_handled_and_does_not_read_as_live()
    {
        Live(SubscriptionStatus.Active);
        await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_seed", StripeEventTypes.SubscriptionCreated));

        _gateway.GetSubscriptionAsync(SubscriptionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StripeSubscriptionSnapshot?>(Snapshot("hibernating")));

        var response = await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent("evt_unknown"));

        var subscription = await _db.Subscriptions.SingleAsync();
        var plan = await PlanNameAsync((await AssignmentAsync())!.PlanId);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(200));
            Assert.That(subscription.Status, Is.EqualTo(SubscriptionStatus.Unknown));
            Assert.That(subscription.IsLive, Is.False);
            Assert.That(plan, Is.EqualTo("free"));
        });
    }

    // ── Negative ─────────────────────────────────────────────────────────────

    /// <summary>The 200 side of the line: a genuine event we understood and cannot act on. No number
    /// of redeliveries will make a plan version appear, and the retries would be indistinguishable
    /// from an outage.</summary>
    [Test]
    public async Task A_price_nothing_here_claims_is_recorded_and_answered_200()
    {
        _gateway.GetSubscriptionAsync(SubscriptionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StripeSubscriptionSnapshot?>(
                Snapshot("active", priceId: "price_made_by_hand")));

        var response = await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_unknownprice", StripeEventTypes.SubscriptionCreated));

        var recorded = await _db.ProcessedStripeEvents.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(200));
            Assert.That(recorded.Outcome, Does.Contain("could not be identified"));
            Assert.That(_db.Subscriptions.Count(), Is.Zero);
            Assert.That(_db.PlanAssignments.Count(), Is.Zero);
        });
    }

    /// <summary>The 5xx side of the line.</summary>
    [Test]
    public void Stripe_being_unreachable_is_not_recorded_and_asks_for_a_redelivery()
    {
        _gateway.GetSubscriptionAsync(SubscriptionId, Arg.Any<CancellationToken>())
            .Returns<Task<StripeSubscriptionSnapshot?>>(
                _ => throw new StripeGatewayException("subscriptions.get", "Stripe is unreachable."));

        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_outage");

        Assert.That(
            async () => await _processor.HandleAsync(
                payload, StripeWebhookFixtures.Sign(payload), CancellationToken.None),
            Throws.InstanceOf<StripeGatewayException>());

        // Never committed, so Stripe's retry is handled rather than dismissed as a duplicate.
        Assert.That(_db.ProcessedStripeEvents.Count(), Is.Zero);
    }

    [Test]
    public async Task A_forged_delivery_is_refused_and_recorded_nowhere()
    {
        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_forged");

        var response = await _processor.HandleAsync(
            payload,
            Stripe.EventUtility.GenerateSignatureHeader(payload, "whsec_somebodyelses"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(400));
            Assert.That(_db.ProcessedStripeEvents.Count(), Is.Zero);
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    [Test]
    public async Task A_delivery_with_no_configured_secret_is_refused_before_anything_is_read()
    {
        Env.License.StripeWebhookSecret = string.Empty;

        var payload = StripeWebhookFixtures.SubscriptionEvent("evt_nosecret");

        var response = await _processor.HandleAsync(
            payload, StripeWebhookFixtures.Sign(payload), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(503));
            Assert.That(_db.ProcessedStripeEvents.Count(), Is.Zero);
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    /// <summary>Runs a sequence of deliveries against a freshly seeded database and answers which
    /// plan the guild ended up on.</summary>
    private async Task<string> ReplayAsync(SubscriptionStatus liveStatus, params string[] payloads)
    {
        await _db.DisposeAsync();
        await PostgresTestDatabase.ResetToEmptyAsync();
        await BuildAsync();

        Live(liveStatus);

        // The subscription has to exist locally before an out-of-order pair means anything, and the
        // creation event is the one that always arrives first in reality.
        Live(SubscriptionStatus.Active);
        await DeliverAsync(StripeWebhookFixtures.SubscriptionEvent(
            "evt_replay_seed", StripeEventTypes.SubscriptionCreated));

        Live(liveStatus);

        foreach (var payload in payloads) await DeliverAsync(payload);

        return await PlanNameAsync((await AssignmentAsync())!.PlanId);
    }

    private async Task<StripeWebhookResponse> DeliverAsync(string payload)
    {
        var response = await _processor.HandleAsync(
            payload, StripeWebhookFixtures.Sign(payload), CancellationToken.None);

        // What Wolverine's transactional middleware does once the endpoint returns.
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return response;
    }

    private void Live(SubscriptionStatus status) =>
        _gateway.GetSubscriptionAsync(SubscriptionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StripeSubscriptionSnapshot?>(Snapshot(WireStatus(status))));

    private static string WireStatus(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Active => "active",
        SubscriptionStatus.Trialing => "trialing",
        SubscriptionStatus.PastDue => "past_due",
        SubscriptionStatus.Canceled => "canceled",
        SubscriptionStatus.Unpaid => "unpaid",
        SubscriptionStatus.Paused => "paused",
        SubscriptionStatus.Incomplete => "incomplete",
        SubscriptionStatus.IncompleteExpired => "incomplete_expired",
        _ => "hibernating",
    };

    private static StripeSubscriptionSnapshot Snapshot(string status, string priceId = ProPriceId) =>
        new(SubscriptionId,
            status,
            CustomerId,
            priceId,
            Start.AddDays(30),
            CancelAtPeriodEnd: false,
            LatestInvoiceId: "in_billingtests",
            new Dictionary<string, string>
            {
                [SubscriptionReconciler.SubjectKindMetadataKey] = nameof(SubjectKind.Guild),
                [SubscriptionReconciler.SubjectIdMetadataKey] = GuildId,
                [SubscriptionReconciler.PayerUserIdMetadataKey] = PayerId,
            });

    private async Task BuildAsync()
    {
        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        await SeedAsync();

        _clock = new TestClock(Start);
        _gateway = Substitute.For<IStripeGateway>();

        _reconciler = new SubscriptionReconciler(
            _db,
            _gateway,
            new StripeCatalogueSync(_db, _gateway),
            new EntitlementVersionService(_db),
            Plans.Options(),
            new StripeOptions(),
            _clock);

        _processor = new StripeWebhookProcessor(_db, _reconciler, _clock);
    }

    private async Task SeedAsync()
    {
        if (await _db.Plans.AnyAsync()) return;

        AddPlan("free", price: null, stripePriceId: null);
        AddPlan("pro", price: 2900, stripePriceId: ProPriceId);

        _db.StripeCustomers.Add(new StripeCustomer
        {
            Id = StripeCustomer.GenerateId(),
            UserId = PayerId,
            StripeCustomerId = CustomerId,
        });

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private void AddPlan(string name, long? price, string? stripePriceId)
    {
        var plan = new Plan
        {
            Id = Plan.GenerateId(),
            Name = name,
            DisplayName = name,
            CurrentVersionNumber = 1,
            CreatedBy = "user_admin",
        };

        _db.Plans.Add(plan);

        _db.PlanVersions.Add(new PlanVersion
        {
            Id = PlanVersion.GenerateId(),
            PlanId = plan.Id,
            VersionNumber = 1,
            ValuesJson = """{"guild.emoji_slots":"50"}""",
            PriceMinorUnits = price,
            Currency = price is null ? null : "usd",
            StripePriceId = stripePriceId,
            Reason = "Seeded by the test.",
            CreatedBy = "user_admin",
        });
    }

    private Task<PlanAssignment?> AssignmentAsync() =>
        _db.PlanAssignments.AsNoTracking().FirstOrDefaultAsync(
            row => row.SubjectKind == Subject.Kind && row.SubjectId == Subject.Id);

    private async Task<string> PlanIdAsync(string name) =>
        (await _db.Plans.AsNoTracking().SingleAsync(plan => plan.Name == name)).Id;

    private async Task<string> PlanNameAsync(string planId) =>
        (await _db.Plans.AsNoTracking().SingleAsync(plan => plan.Id == planId)).Name;
}
