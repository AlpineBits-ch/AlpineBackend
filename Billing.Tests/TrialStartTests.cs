using AppEnvironment;
using Billing.Application.Dtos;
using Billing.Application.Promotions;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Wolverine;

namespace Billing.Tests;

/// <summary>Starting a trial, against a real Postgres and a substituted Stripe.</summary>
[TestFixture]
public class TrialStartTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private const string Fingerprint = "fp_a_real_physical_card";

    private string _originalSecretKey = string.Empty;
    private string _originalMode = string.Empty;

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private PromotionCampaignService _campaigns = null!;
    private IStripeGateway _gateway = null!;
    private IMessageBus _bus = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        // Both, and both restored.
        _originalSecretKey = Env.License.StripeSecretKey;
        _originalMode = Env.License.Mode;
        Env.License.StripeSecretKey = PromotionFixtures.StripeSecretKey;
        Env.License.Mode = LicenseConfiguration.Hosted;

        _clock = new TestClock(Now);
        _campaigns = PromotionFixtures.Campaigns(_db, _clock);
        _gateway = PromotionFixtures.StripeSaying(Fingerprint);
        _bus = PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying());

        await PromotionFixtures.SeedPlansAsync(_db, Now);

        // The card goes on first, through the flow that actually attaches one.
        await AttachCardAsync();
    }

    /// <summary>Adds a card the way a client does: the SetupIntent route, which creates the Stripe
    /// customer the fingerprint is later read against.</summary>
    private async Task AttachCardAsync(string owner = PromotionFixtures.Owner)
    {
        var cards = new PaymentMethodService(
            _db, _gateway, new StripeCustomerRegistry(_db, _gateway));

        await cards.CreateSetupIntentAsync(owner, CancellationToken.None);
        await _db.SaveChangesAsync();
    }

    [TearDown]
    public async Task Dispose()
    {
        Env.License.StripeSecretKey = _originalSecretKey;
        Env.License.Mode = _originalMode;
        await _db.DisposeAsync();
    }

    private TrialService Trials() =>
        PromotionFixtures.Trials(_db, _clock, _gateway, _bus, _campaigns);

    private async Task<PromotionCampaign> OpenAsync(
        IReadOnlyList<string>? rules = null, int trialDays = 30, long budget = 100)
    {
        var campaign = await _campaigns.CreateAsync(
            PromotionFixtures.Open(rules: rules, trialDays: trialDays, budget: budget),
            PromotionFixtures.Staff,
            CancellationToken.None);

        await _db.SaveChangesAsync();
        return campaign;
    }

    private async Task<TrialStartResult> StartAsync(
        PromotionCampaign campaign,
        string owner = PromotionFixtures.Owner,
        string? guildId = PromotionFixtures.Guild,
        string? paymentMethodId = PromotionFixtures.PaymentMethodId)
    {
        var started = await Trials().StartAsync(
            campaign.Code, guildId, paymentMethodId, owner, CancellationToken.None);

        await _db.SaveChangesAsync();
        return started;
    }

    /// <summary>
    /// The cap is enforced before Stripe is called, not after. Charging once the subscription
    /// existed meant an exhausted campaign still created a live trial with the caller's card while
    /// answering 409, because the endpoint catches the refusal and returns normally.
    /// </summary>
    [Test]
    public async Task An_exhausted_campaign_refuses_before_stripe_is_touched()
    {
        var campaign = await OpenAsync(budget: 1);
        await AttachCardAsync();
        await StartAsync(campaign);

        _gateway.ClearReceivedCalls();

        Assert.That(
            async () => await StartAsync(campaign, owner: "user_second", guildId: "gild_second"),
            Throws.InstanceOf<PromotionRefusedException>());

        await _gateway.DidNotReceive().CreateSubscriptionAsync(
            Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());
    }

    // ── The happy path ───────────────────────────────────────────────────────

    [Test]
    public async Task A_trial_creates_a_trialing_subscription_and_returns_its_clock()
    {
        var campaign = await OpenAsync();

        var started = await StartAsync(campaign);

        Assert.Multiple(() =>
        {
            Assert.That(started.Subscription.Status, Is.EqualTo("trialing"),
                "a trial is live the moment Stripe creates it, unlike a purchase");
            Assert.That(started.Subscription.SubjectId, Is.EqualTo(PromotionFixtures.Guild));
            Assert.That(started.Owner.EndsAt, Is.EqualTo(Now.AddDays(30)));
            Assert.That(started.ClientSecret, Is.EqualTo("seti_promo_secret"),
                "a trial's secret is the pending setup intent's, because its first invoice is zero");
        });
    }

    /// <summary>Both rows, because either alone is farmable: the account row makes a new guild every
    /// month pointless, and the guild row survives the guild changing hands.</summary>
    [Test]
    public async Task A_trial_writes_the_owner_row_the_guild_row_and_the_card_mark()
    {
        var campaign = await OpenAsync();

        await StartAsync(campaign);
        _db.ChangeTracker.Clear();

        var rows = await _db.PromotionRedemptions.ToListAsync();
        var marks = await _db.PromotionIdentityMarks.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(row => row.SubjectKind),
                Is.EquivalentTo(new[] { SubjectKind.User, SubjectKind.Guild }));

            Assert.That(rows.Single(row => row.SubjectKind == SubjectKind.User).StripeSubscriptionId,
                Is.EqualTo(PromotionFixtures.TrialSubscriptionId),
                "the owner row carries the subscription, so a move knows what to carry with it");

            Assert.That(marks.Any(mark => mark.Kind == PromotionIdentityKind.Card), Is.True);
            Assert.That(marks.Any(mark => mark.Kind == PromotionIdentityKind.Phone), Is.True);
            Assert.That(marks.Any(mark => mark.Kind == PromotionIdentityKind.Device), Is.True);
        });
    }

    /// <summary>The mark is the keyed hash and never the value.</summary>
    [Test]
    public async Task The_card_mark_is_the_hash_and_never_the_fingerprint()
    {
        var campaign = await OpenAsync();

        await StartAsync(campaign);
        _db.ChangeTracker.Clear();

        var card = await _db.PromotionIdentityMarks.SingleAsync(
            mark => mark.Kind == PromotionIdentityKind.Card);

        Assert.Multiple(() =>
        {
            Assert.That(card.Hash, Is.Not.EqualTo(Fingerprint));
            Assert.That(card.Hash, Is.EqualTo(
                PromotionFixtures.Hasher().Of(PromotionIdentityKind.Card, Fingerprint)));
        });
    }

    /// <summary>Stripe runs the clock, so the number of days and the card that will be billed after it
    /// both have to reach the create call. A trial created without <c>trial_period_days</c> charges
    /// somebody immediately, which is the loudest possible version of this bug and still worth
    /// pinning.</summary>
    [Test]
    public async Task The_create_carries_the_trial_length_and_the_card_that_follows_it()
    {
        var campaign = await OpenAsync(trialDays: 14);

        await StartAsync(campaign);

        await _gateway.Received(1).CreateSubscriptionAsync(
            Arg.Is<StripeSubscriptionRequest>(request =>
                request.TrialPeriodDays == 14
                && request.DefaultPaymentMethodId == PromotionFixtures.PaymentMethodId
                && request.PriceId == PromotionFixtures.StripePriceId),
            Arg.Any<StripeIdempotencyKey>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The campaign rides along in Stripe's metadata so the dashboard can answer "why is this
    /// one free" without anybody opening our console.</summary>
    [Test]
    public async Task The_create_names_the_campaign_and_the_subject_in_stripe_metadata()
    {
        var campaign = await OpenAsync();

        await StartAsync(campaign);

        await _gateway.Received(1).CreateSubscriptionAsync(
            Arg.Is<StripeSubscriptionRequest>(request =>
                request.Metadata[TrialService.CampaignMetadataKey] == campaign.Code
                && request.Metadata[SubscriptionReconciler.SubjectIdMetadataKey]
                    == PromotionFixtures.Guild),
            Arg.Any<StripeIdempotencyKey>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The budget is charged by the redemption rather than by the caller, so it holds for
    /// every path that ever exists - including this one, which is the first real one.</summary>
    [Test]
    public async Task A_trial_charges_the_campaign_budget_once()
    {
        var campaign = await OpenAsync();

        await StartAsync(campaign);
        _db.ChangeTracker.Clear();

        Assert.That((await _db.PromotionCampaigns.SingleAsync()).IssuedRedemptions, Is.EqualTo(1));
    }

    /// <summary><b>The plan is switched on by the webhook and not here.</b> A trial subscription is
    /// live the instant Stripe creates it, which makes granting from the create response look safe -
    /// and it is the one place in this service where a plan would be switched on because a create call
    /// returned rather than because Stripe said so.</summary>
    [Test]
    public async Task Starting_a_trial_assigns_no_plan_by_itself()
    {
        var campaign = await OpenAsync();

        await StartAsync(campaign);
        _db.ChangeTracker.Clear();

        var assignments = await _db.PlanAssignments.CountAsync();
        var subscription = await _db.Subscriptions.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(assignments, Is.Zero);
            Assert.That(subscription.Status, Is.EqualTo(SubscriptionStatus.Trialing));
        });
    }

    // ── The card, and the ordering ───────────────────────────────────────────

    /// <summary>The WP-19 fails-closed test, now passing for the right reason.</summary>
    [Test]
    public async Task A_campaign_requiring_a_card_admits_an_applicant_who_has_one()
    {
        var campaign = await OpenAsync(rules: ["payment_card"]);

        var started = await StartAsync(campaign);

        Assert.That(started.Subscription.Status, Is.EqualTo("trialing"));
    }

    [Test]
    public async Task A_campaign_requiring_a_card_refuses_an_account_with_none()
    {
        _gateway = PromotionFixtures.StripeSaying(fingerprint: null);
        var campaign = await OpenAsync(rules: ["payment_card"]);

        var refusal = Assert.ThrowsAsync<PromotionRefusedException>(
            async () => await StartAsync(campaign));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(PromotionErrorCodes.NotEligible));
            Assert.That(refusal.FailedRules, Is.EquivalentTo(new[] { "payment_card" }));
        });
    }

    /// <summary><b>Attach, read, decide, create - in that order.</b> The fingerprint is read before
    /// anything is created, which is the difference between a control and a detection. If the create
    /// ever moves in front of the decision this test is the one that notices, because everything a
    /// caller can see would otherwise look identical.</summary>
    [Test]
    public async Task A_refused_trial_creates_nothing_in_stripe()
    {
        var campaign = await OpenAsync();

        await StartAsync(campaign);
        _db.ChangeTracker.Clear();
        _gateway.ClearReceivedCalls();

        Assert.ThrowsAsync<PromotionRefusedException>(
            async () => await StartAsync(campaign, guildId: "gild_a_brand_new_one"));

        await _gateway.DidNotReceive().CreateSubscriptionAsync(
            Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(),
            Arg.Any<CancellationToken>());

        await _gateway.Received().GetCardIdentityAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── The refusals a person can act on ─────────────────────────────────────

    [Test]
    public async Task A_guild_campaign_with_no_guild_named_is_refused()
    {
        var campaign = await OpenAsync();

        var refusal = Assert.ThrowsAsync<PromotionRefusedException>(
            async () => await StartAsync(campaign, guildId: null));

        Assert.That(refusal!.Code, Is.EqualTo(PromotionErrorCodes.TargetRequired));
    }

    /// <summary>Managing the server is what it takes to put it on a plan, asked of Guild per request
    /// rather than read from a claim. An outage answers no, which is a refusal - the same direction
    /// the purchase path fails in, and for the same reason.</summary>
    [Test]
    public async Task Somebody_who_does_not_manage_the_guild_is_refused()
    {
        _bus = PromotionFixtures.AllowingGuild(PromotionFixtures.IdentitySaying(), allowed: false);
        var campaign = await OpenAsync();

        var refusal = Assert.ThrowsAsync<PromotionRefusedException>(
            async () => await StartAsync(campaign));

        Assert.That(refusal!.Code, Is.EqualTo(PromotionErrorCodes.TargetRequired));
    }

    /// <summary>A guild already paying for something gains nothing from a free plan and would end up
    /// with two live subscriptions, which the filtered unique index refuses - after the trial had
    /// already been created in Stripe.</summary>
    [Test]
    public async Task A_guild_that_is_already_subscribed_is_refused()
    {
        var campaign = await OpenAsync();
        await SeedLiveSubscriptionAsync();

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await StartAsync(campaign));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.AlreadySubscribed));
    }

    [Test]
    public async Task A_paused_campaign_refuses_before_anything_is_created()
    {
        var campaign = await OpenAsync();

        await _campaigns.PauseAsync(campaign.Code, PromotionFixtures.Staff, CancellationToken.None);
        await _db.SaveChangesAsync();

        var refusal = Assert.ThrowsAsync<PromotionRefusedException>(
            async () => await StartAsync(campaign));

        Assert.That(refusal!.Code, Is.EqualTo(PromotionErrorCodes.CampaignClosed));

        await _gateway.DidNotReceive().CreateSubscriptionAsync(
            Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void A_campaign_that_does_not_exist_is_a_refusal_rather_than_a_failure()
    {
        var refusal = Assert.ThrowsAsync<PromotionRefusedException>(
            async () => await Trials().StartAsync(
                "no-such-campaign", PromotionFixtures.Guild, null, PromotionFixtures.Owner,
                CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(PromotionErrorCodes.UnknownCampaign));
    }

    /// <summary>The evidence that the Stripe gate on this path is live rather than decorative. Without
    /// it every assertion in this fixture would pass just as happily against an instance that sells
    /// nothing.</summary>
    [Test]
    public async Task An_instance_with_no_secret_key_refuses_the_trial_as_billing_disabled()
    {
        var campaign = await OpenAsync();
        Env.License.StripeSecretKey = string.Empty;

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await StartAsync(campaign));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.BillingDisabled));
    }

    /// <summary>A campaign naming a plan that cannot be subscribed to fails loudly rather than
    /// conferring nothing. The same check and the same sentence a purchase gets, through the same
    /// method, because "sellable" must not mean two things.</summary>
    [Test]
    public async Task A_campaign_naming_an_unsellable_plan_is_refused()
    {
        var campaign = await _campaigns.CreateAsync(
            PromotionFixtures.Open() with { Plan = Plans.Free },
            PromotionFixtures.Staff,
            CancellationToken.None);

        await _db.SaveChangesAsync();

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await StartAsync(campaign));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotPurchasable));
    }

    private async Task SeedLiveSubscriptionAsync()
    {
        var plan = await _db.Plans.FirstAsync(row => row.Name == Plans.Pro);

        _db.Subscriptions.Add(new Subscription
        {
            Id = Subscription.GenerateId(),
            StripeSubscriptionId = "sub_already_paying",
            PayerUserId = PromotionFixtures.Owner,
            SubjectKind = SubjectKind.Guild,
            SubjectId = PromotionFixtures.Guild,
            PlanId = plan.Id,
            VersionNumber = 1,
            Status = SubscriptionStatus.Active,
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        await _db.SaveChangesAsync();
    }
}
