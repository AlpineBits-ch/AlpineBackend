using AppEnvironment;
using Billing.Application.Dtos;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Wolverine;

namespace Billing.Tests;

/// <summary>
/// Buying, reading and managing a subscription, against a real Postgres and a substituted Stripe.
/// </summary>
[TestFixture]
public class CheckoutSurfaceTests
{
    private const string SecretKey = "sk_test_billingtests";
    private const string Payer = "user_payer";
    private const string Manager = "user_manager";
    private const string Stranger = "user_stranger";
    private const string GuildId = "gld_test";

    private string _originalSecretKey = string.Empty;
    private MicroserviceContext _db = null!;
    private IStripeGateway _gateway = null!;
    private IMessageBus _bus = null!;
    private SubscriptionCheckoutService _checkout = null!;
    private CheckoutCatalogueService _catalogue = null!;
    private PaymentMethodService _cards = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        _originalSecretKey = Env.License.StripeSecretKey;
        Env.License.StripeSecretKey = SecretKey;

        _gateway = Substitute.For<IStripeGateway>();
        _bus = Substitute.For<IMessageBus>();

        _gateway.CreateCustomerAsync(
                Arg.Any<StripeCustomerRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StripeObjectRef("cus_test")));

        _gateway.CreateSubscriptionAsync(
                Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StripeSubscriptionResult(
                Snapshot("sub_test", "incomplete"), "pi_test_secret_abc")));

        AllowManageGuild(true);

        _bus.InvokeAsync<ListManageableGuildsForUserResponse>(
                Arg.Any<ListManageableGuildsForUserRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ListManageableGuildsForUserResponse());

        var options = new EntitlementPlanOptions { DefaultGuildPlan = "free", DefaultUserPlan = "free_user" };

        _catalogue = new CheckoutCatalogueService(_db, options);
        _cards = new PaymentMethodService(_db, _gateway, new StripeCustomerRegistry(_db, _gateway));

        _checkout = new SubscriptionCheckoutService(
            _db, _gateway, new StripeCustomerRegistry(_db, _gateway), options, _bus);
    }

    [TearDown]
    public async Task Dispose()
    {
        Env.License.StripeSecretKey = _originalSecretKey;
        await _db.DisposeAsync();
    }

    private void AllowManageGuild(bool allowed) =>
        _bus.InvokeAsync<HasUserPermissionToGuildResponse>(
                Arg.Any<HasUserPermissionToGuildRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new HasUserPermissionToGuildResponse
            {
                IsAllowed = allowed,
                Permission = Guild.Contracts.ExternalPermission.ManageGuild,
            });

    private static StripeSubscriptionSnapshot Snapshot(
        string id, string status, bool cancelAtPeriodEnd = false) =>
        new(id, status, "cus_test", "price_pro", new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            cancelAtPeriodEnd, "in_test", new Dictionary<string, string>());

    private async Task<Plan> SeedPlanAsync(
        string name = "pro",
        long? price = 2900,
        string values = "{\"voice.max_participants\":\"75\"}",
        string? stripePriceId = "price_pro")
    {
        var plan = new Plan
        {
            Id = Plan.GenerateId(),
            Name = name,
            DisplayName = name.ToUpperInvariant(),
            CurrentVersionNumber = 1,
            CreatedBy = "system",
            StripeProductId = "prod_test",
        };

        _db.Plans.Add(plan);
        _db.PlanVersions.Add(new PlanVersion
        {
            Id = PlanVersion.GenerateId(),
            PlanId = plan.Id,
            VersionNumber = 1,
            ValuesJson = values,
            PriceMinorUnits = price,
            Currency = price is null ? null : "usd",
            StripePriceId = price is null ? null : stripePriceId,
            Reason = "Launch.",
            CreatedBy = "system",
        });

        await _db.SaveChangesAsync();
        return plan;
    }

    private async Task<Subscription> SeedSubscriptionAsync(
        Plan plan, SubscriptionStatus status = SubscriptionStatus.Active, string payer = Payer)
    {
        var subscription = new Subscription
        {
            Id = Subscription.GenerateId(),
            StripeSubscriptionId = "sub_existing",
            PayerUserId = payer,
            SubjectKind = SubjectKind.Guild,
            SubjectId = GuildId,
            PlanId = plan.Id,
            VersionNumber = 1,
            Status = status,
            CurrentPeriodEnd = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
        };

        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync();

        return subscription;
    }

    // ── The catalogue ────────────────────────────────────────────────────────

    [Test]
    public async Task The_catalogue_publishes_the_current_version_with_its_price_and_entitlements()
    {
        await SeedPlanAsync();

        var catalogue = await _catalogue.ReadAsync(CancellationToken.None);
        var pro = catalogue.Plans.Single();

        Assert.Multiple(() =>
        {
            Assert.That(catalogue.Enabled, Is.True);
            Assert.That(catalogue.Currency, Is.EqualTo("usd"));
            Assert.That(pro.Name, Is.EqualTo("pro"));
            Assert.That(pro.PriceMinorUnits, Is.EqualTo(2900));
            Assert.That(pro.Purchasable, Is.True);
            Assert.That(pro.SubjectKind, Is.EqualTo(SubjectKind.Guild));
            Assert.That(pro.Interval, Is.EqualTo("month"));
            Assert.That(pro.Entitlements.ContainsKey("voice.max_participants"), Is.True);
        });
    }

    /// <summary>Free is on the comparison table and is not sold.</summary>
    [Test]
    public async Task An_unpriced_plan_is_published_and_not_purchasable()
    {
        await SeedPlanAsync("free", price: null);

        var free = (await _catalogue.ReadAsync(CancellationToken.None)).Plans.Single();

        Assert.Multiple(() =>
        {
            Assert.That(free.Purchasable, Is.False);
            Assert.That(free.PriceMinorUnits, Is.Null);
            Assert.That(free.Currency, Is.Null);
        });
    }

    [Test]
    public async Task A_user_plan_is_published_as_a_user_plan_with_its_own_keys()
    {
        await SeedPlanAsync("venta_plus", 600,
            "{\"user.max_devices\":\"10\",\"voice.video_ceiling\":\"2160p60\"}");

        var plan = (await _catalogue.ReadAsync(CancellationToken.None)).Plans.Single();

        Assert.Multiple(() =>
        {
            Assert.That(plan.SubjectKind, Is.EqualTo(SubjectKind.User));
            Assert.That(plan.Entitlements.Keys,
                Is.EquivalentTo(new[] { "user.max_devices", "voice.video_ceiling" }));
            Assert.That(plan.Entitlements["voice.video_ceiling"].Rung, Is.EqualTo("2160p60"));
        });
    }

    /// <summary>The whole point of publishing plans on an instance with no key: the comparison table
    /// still renders and only the buy buttons are absent.</summary>
    [Test]
    public async Task An_instance_with_no_secret_key_still_publishes_the_plans()
    {
        Env.License.StripeSecretKey = string.Empty;
        await SeedPlanAsync();

        var catalogue = await _catalogue.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(catalogue.Enabled, Is.False);
            Assert.That(catalogue.Plans, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task An_archived_plan_is_absent_from_the_catalogue()
    {
        var plan = await SeedPlanAsync();
        plan.Archive("user_admin", "Withdrawn.", DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync();

        var catalogue = await _catalogue.ReadAsync(CancellationToken.None);

        Assert.That(catalogue.Plans, Is.Empty);
    }

    /// <summary>A guild plan mentioning a user key is harmless - the resolver drops it - but
    /// publishing it would put a row in the comparison table that nobody buying this plan gets.
    /// </summary>
    [Test]
    public async Task A_key_from_the_wrong_scope_is_left_off_the_plan()
    {
        await SeedPlanAsync("pro", 2900,
            "{\"voice.max_participants\":\"75\",\"user.max_devices\":\"10\"}");

        var plan = (await _catalogue.ReadAsync(CancellationToken.None)).Plans.Single();

        Assert.Multiple(() =>
        {
            Assert.That(plan.SubjectKind, Is.EqualTo(SubjectKind.Guild));
            Assert.That(plan.Entitlements.ContainsKey("user.max_devices"), Is.False);
        });
    }

    // ── Buying ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Buying_a_guild_plan_writes_a_row_and_returns_the_client_secret()
    {
        var plan = await SeedPlanAsync();

        var response = await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None);

        await _db.SaveChangesAsync();

        var row = await _db.Subscriptions.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.ClientSecret, Is.EqualTo("pi_test_secret_abc"));
            Assert.That(response.Subscription.Status, Is.EqualTo("incomplete"));
            Assert.That(response.Subscription.IsPayer, Is.True);
            Assert.That(row.PayerUserId, Is.EqualTo(Payer));
            Assert.That(row.SubjectId, Is.EqualTo(GuildId));
            Assert.That(row.PlanId, Is.EqualTo(plan.Id));
            Assert.That(row.StripeSubscriptionId, Is.EqualTo("sub_test"));
        });
    }

    /// <summary>Both directions of identity, keyed by the consts the reader declares.</summary>
    [Test]
    public async Task Our_ids_go_into_the_subscription_metadata_under_the_keys_the_reconciler_reads()
    {
        await SeedPlanAsync();

        await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None);

        var request = (StripeSubscriptionRequest)_gateway.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateSubscriptionAsync))
            .GetArguments()[0]!;

        Assert.Multiple(() =>
        {
            Assert.That(request.Metadata[SubscriptionReconciler.SubjectKindMetadataKey], Is.EqualTo("guild"));
            Assert.That(request.Metadata[SubscriptionReconciler.SubjectIdMetadataKey], Is.EqualTo(GuildId));
            Assert.That(request.Metadata[SubscriptionReconciler.PayerUserIdMetadataKey], Is.EqualTo(Payer));
            Assert.That(request.Metadata[StripeCatalogueSync.PlanMetadataKey], Is.EqualTo("pro"));
            Assert.That(request.Metadata[StripeCatalogueSync.PlanVersionMetadataKey], Is.EqualTo("1"));
        });
    }

    /// <summary>
    /// The whole loop: what checkout writes into Stripe is what the webhook reads back out.
    /// </summary>
    [Test]
    public async Task Metadata_written_at_checkout_resolves_the_subject_when_the_webhook_reads_it()
    {
        var plan = await SeedPlanAsync();
        var version = await _db.PlanVersions.AsNoTracking().SingleAsync();

        await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None);
        await _db.SaveChangesAsync();

        var written = (StripeSubscriptionRequest)_gateway.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateSubscriptionAsync))
            .GetArguments()[0]!;

        // Stripe now reports the subscription as live, carrying exactly the metadata checkout sent.
        _gateway.GetSubscriptionAsync("sub_test", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StripeSubscriptionSnapshot?>(new StripeSubscriptionSnapshot(
                "sub_test", "active", "cus_test", version.StripePriceId,
                new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero), false, "in_test",
                written.Metadata)));

        var reconciler = new SubscriptionReconciler(
            _db,
            _gateway,
            new StripeCatalogueSync(_db, _gateway),
            new EntitlementVersionService(_db),
            new EntitlementPlanOptions { DefaultGuildPlan = "free", DefaultUserPlan = "free_user" },
            new StripeOptions(),
            TimeProvider.System);

        await reconciler.ReconcileAsync(
            "sub_test", StripeDunningSignal.None, "customer.subscription.created",
            DateTimeOffset.UtcNow, CancellationToken.None);

        await _db.SaveChangesAsync();

        var assignment = await _db.PlanAssignments.AsNoTracking().SingleOrDefaultAsync();

        Assert.Multiple(() =>
        {
            Assert.That(assignment, Is.Not.Null,
                "the webhook could not work out which guild the subscription pays for");

            Assert.That(assignment!.SubjectKind, Is.EqualTo(SubjectKind.Guild));
            Assert.That(assignment.SubjectId, Is.EqualTo(GuildId));
            Assert.That(assignment.PlanId, Is.EqualTo(plan.Id));
        });
    }

    /// <summary>The address goes to Stripe and is stored nowhere here.</summary>
    [Test]
    public async Task A_billing_address_reaches_Stripe_and_is_not_stored_locally()
    {
        await SeedPlanAsync();

        await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId,
                new BillingAddressDto("DE", "Beispielweg 1", null, "Berlin", null, "10115")),
            Payer, CancellationToken.None);

        await _db.SaveChangesAsync();

        var customerRequest = (StripeCustomerRequest)_gateway.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateCustomerAsync))
            .GetArguments()[0]!;

        var stored = await _db.StripeCustomers.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(customerRequest.Address!.Country, Is.EqualTo("DE"));
            Assert.That(customerRequest.Address.PostalCode, Is.EqualTo("10115"));

            // Two columns and no third. The entity has nowhere to put an address, which is the point.
            Assert.That(stored.UserId, Is.EqualTo(Payer));
            Assert.That(stored.StripeCustomerId, Is.EqualTo("cus_test"));
        });
    }

    [Test]
    public async Task A_second_purchase_for_the_same_guild_is_refused_before_Stripe_is_called()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan);

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.AlreadySubscribed));
            Assert.That(refusal.StatusCode, Is.EqualTo(409));
            Assert.That(
                _gateway.ReceivedCalls().Any(
                    call => call.GetMethodInfo().Name == nameof(IStripeGateway.CreateSubscriptionAsync)),
                Is.False);
        });
    }

    /// <summary>A cancelled subscription must not block a re-subscribe.</summary>
    [Test]
    public async Task A_cancelled_subscription_does_not_block_buying_again()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.Canceled);

        var response = await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None);

        await _db.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.Subscription.Id, Is.EqualTo("sub_test"));
            Assert.That(_db.Subscriptions.Count(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Buying_for_a_guild_without_ManageGuild_is_refused()
    {
        await SeedPlanAsync();
        AllowManageGuild(false);

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Stranger,
                CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotPermitted));
            Assert.That(refusal.StatusCode, Is.EqualTo(403));
        });
    }

    /// <summary>Guild being down leaves the answer no. Failing open here would let anybody put any
    /// guild on a paid plan during an outage.</summary>
    [Test]
    public async Task A_Guild_outage_refuses_the_purchase_rather_than_assuming_permission()
    {
        await SeedPlanAsync();

        _bus.InvokeAsync<HasUserPermissionToGuildResponse>(
                Arg.Any<HasUserPermissionToGuildRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns<Task<HasUserPermissionToGuildResponse>>(_ => throw new TimeoutException("Guild is down."));

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Manager,
                CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotPermitted));
    }

    [Test]
    public async Task A_user_plan_can_only_be_bought_for_oneself()
    {
        await SeedPlanAsync("venta_plus", 600, "{\"user.max_devices\":\"10\"}");

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("venta_plus", SubjectKind.User, "user_somebody_else"),
                Payer, CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotPermitted));
    }

    /// <summary><c>me</c> is a client-side sentinel in <c>EntitlementSubjectRef</c> and must never be
    /// accepted on the wire. It is refused rather than resolved, because a server that quietly
    /// substituted the caller for whatever arrived in that field would also quietly substitute them
    /// for somebody else's id.</summary>
    [Test]
    public async Task The_client_side_me_sentinel_is_not_a_subject_id()
    {
        await SeedPlanAsync("venta_plus", 600, "{\"user.max_devices\":\"10\"}");

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("venta_plus", SubjectKind.User, "me"), Payer,
                CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotPermitted));
    }

    [Test]
    public async Task A_guild_plan_cannot_be_bought_for_a_person()
    {
        await SeedPlanAsync();

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("pro", SubjectKind.User, Payer), Payer, CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotPurchasable));
    }

    [Test]
    public async Task A_plan_with_no_price_cannot_be_bought()
    {
        await SeedPlanAsync("free", price: null);

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("free", SubjectKind.Guild, GuildId), Payer,
                CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotPurchasable));
            Assert.That(refusal.StatusCode, Is.EqualTo(400));
        });
    }

    /// <summary>The state the backfill exists to remove.</summary>
    [Test]
    public async Task A_priced_plan_with_no_Stripe_price_cannot_be_bought()
    {
        await SeedPlanAsync("pro", 2900, stripePriceId: null);

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer,
                CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotPurchasable));
    }

    [Test]
    public async Task An_instance_with_no_secret_key_refuses_the_purchase_as_billing_disabled()
    {
        Env.License.StripeSecretKey = string.Empty;
        await SeedPlanAsync();

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer,
                CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.BillingDisabled));
            Assert.That(refusal.StatusCode, Is.EqualTo(404));
        });
    }

    // ── Reopening an abandoned checkout ──────────────────────────────────────

    private void StripeSaysIncomplete(string status = "incomplete", string? secret = "pi_resumed_secret") =>
        _gateway.GetSubscriptionWithSecretAsync("sub_existing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StripeSubscriptionResult?>(
                new StripeSubscriptionResult(Snapshot("sub_existing", status), secret)));

    /// <summary>The case that stops a double charge.</summary>
    [Test]
    public async Task Reopening_checkout_resumes_the_incomplete_attempt_rather_than_starting_a_second()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.Incomplete);
        StripeSaysIncomplete();

        var response = await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None);

        await _db.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.Subscription.Id, Is.EqualTo("sub_existing"));
            Assert.That(response.ClientSecret, Is.EqualTo("pi_resumed_secret"));
            Assert.That(_db.Subscriptions.Count(), Is.EqualTo(1));
        });

        await _gateway.DidNotReceive().CreateSubscriptionAsync(
            Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An incomplete subscription is an unfinished attempt, not a subscription.</summary>
    [Test]
    public async Task An_incomplete_attempt_is_never_already_subscribed()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.Incomplete);
        StripeSaysIncomplete();

        Assert.That(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer,
                CancellationToken.None),
            Throws.Nothing);
    }

    /// <summary>An abandoned attempt for a plan they decided against must not stay confirmable from a
    /// stale client secret in a background tab, so it is cancelled outright rather than left for
    /// Stripe to expire.</summary>
    [Test]
    public async Task An_incomplete_attempt_for_another_plan_is_cancelled_and_a_new_one_created()
    {
        var pro = await SeedPlanAsync();
        var plus = await SeedPlanAsync("plus", 900, stripePriceId: "price_plus");
        await SeedSubscriptionAsync(plus, SubscriptionStatus.Incomplete);
        StripeSaysIncomplete();

        var response = await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None);

        await _db.SaveChangesAsync();

        var abandoned = await _db.Subscriptions.AsNoTracking()
            .SingleAsync(subscription => subscription.StripeSubscriptionId == "sub_existing");

        await _gateway.Received(1).CancelIncompleteSubscriptionAsync(
            "sub_existing", Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());

        Assert.Multiple(() =>
        {
            Assert.That(response.Subscription.Id, Is.EqualTo("sub_test"));
            Assert.That(response.Subscription.PlanName, Is.EqualTo("pro"));
            Assert.That(abandoned.Status, Is.EqualTo(SubscriptionStatus.Canceled));
            Assert.That(pro.Id, Is.Not.EqualTo(plus.Id));
        });
    }

    /// <summary>Stripe expires an incomplete subscription after about 23 hours and tells us nothing at
    /// the moment it happens, so a local row saying "incomplete" is a claim about the past. Asking
    /// Stripe is what keeps a day-old row from blocking a purchase forever.</summary>
    [Test]
    public async Task An_expired_attempt_is_reconciled_and_a_new_subscription_created()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.Incomplete);
        StripeSaysIncomplete("incomplete_expired", secret: null);

        var response = await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None);

        await _db.SaveChangesAsync();

        var stale = await _db.Subscriptions.AsNoTracking()
            .SingleAsync(subscription => subscription.StripeSubscriptionId == "sub_existing");

        Assert.Multiple(() =>
        {
            Assert.That(response.Subscription.Id, Is.EqualTo("sub_test"));
            Assert.That(stale.Status, Is.EqualTo(SubscriptionStatus.IncompleteExpired));
        });
    }

    [Test]
    public async Task An_attempt_Stripe_has_never_heard_of_is_written_off_and_a_new_one_created()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.Incomplete);

        _gateway.GetSubscriptionWithSecretAsync("sub_existing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StripeSubscriptionResult?>(null));

        var response = await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None);

        await _db.SaveChangesAsync();

        var stale = await _db.Subscriptions.AsNoTracking()
            .SingleAsync(subscription => subscription.StripeSubscriptionId == "sub_existing");

        Assert.Multiple(() =>
        {
            Assert.That(response.Subscription.Id, Is.EqualTo("sub_test"));
            Assert.That(stale.Status, Is.EqualTo(SubscriptionStatus.Canceled));
        });
    }

    /// <summary>Somebody else started the attempt.</summary>
    [Test]
    public async Task An_incomplete_attempt_by_another_payer_is_not_resumed()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.Incomplete, payer: Manager);
        StripeSaysIncomplete();

        var response = await _checkout.CreateAsync(
            new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Subscription.Id, Is.EqualTo("sub_test"));
            Assert.That(response.ClientSecret, Is.EqualTo("pi_test_secret_abc"));
        });
    }

    /// <summary>The row said incomplete and Stripe says it went live - a webhook we have not processed
    /// yet. That genuinely is already subscribed, and it is the one place the duplicate check can see
    /// it.</summary>
    [Test]
    public async Task An_attempt_Stripe_says_is_live_refuses_as_already_subscribed()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.Incomplete);
        StripeSaysIncomplete("active");

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CreateAsync(
                new CreateSubscriptionRequest("pro", SubjectKind.Guild, GuildId), Payer,
                CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.AlreadySubscribed));
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    [Test]
    public async Task The_payer_reads_their_own_subscription_as_the_payer()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan);

        var dto = await _checkout.GetAsync("sub_existing", Payer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(dto.IsPayer, Is.True);
            Assert.That(dto.PlanName, Is.EqualTo("pro"));
            Assert.That(dto.PriceMinorUnits, Is.EqualTo(2900));
            Assert.That(dto.Status, Is.EqualTo("active"));
        });
    }

    /// <summary>A guild manager may look at what their guild is on and may not touch somebody else's
    /// card, which is exactly what <c>isPayer</c> tells the client to render.</summary>
    [Test]
    public async Task A_guild_manager_who_is_not_the_payer_may_look_and_is_told_so()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan);

        var dto = await _checkout.GetAsync("sub_existing", Manager, CancellationToken.None);

        Assert.That(dto.IsPayer, Is.False);
    }

    [Test]
    public async Task Somebody_with_no_claim_on_it_gets_the_same_answer_as_a_missing_subscription()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan);
        AllowManageGuild(false);

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.GetAsync("sub_existing", Stranger, CancellationToken.None));

        var missing = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.GetAsync("sub_does_not_exist", Payer, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.UnknownSubscription));
            Assert.That(refusal.StatusCode, Is.EqualTo(404));
            Assert.That(missing!.Code, Is.EqualTo(refusal.Code));
        });
    }

    [Test]
    public async Task The_list_includes_guilds_the_caller_manages_as_well_as_what_they_pay_for()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, payer: Payer);

        _bus.InvokeAsync<ListManageableGuildsForUserResponse>(
                Arg.Any<ListManageableGuildsForUserRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ListManageableGuildsForUserResponse
            {
                Guilds = [new ManageableGuildSummary { Id = GuildId, Name = "Test" }],
            });

        var mine = await _checkout.ListAsync(Payer, CancellationToken.None);
        var theirs = await _checkout.ListAsync(Manager, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(mine.Single().IsPayer, Is.True);
            Assert.That(theirs.Single().IsPayer, Is.False);
        });
    }

    // ── Cancel and resume ────────────────────────────────────────────────────

    [Test]
    public async Task Cancelling_sets_the_period_end_flag_rather_than_ending_anything_today()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan);

        _gateway.SetCancelAtPeriodEndAsync(
                "sub_existing", true, Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Snapshot("sub_existing", "active", cancelAtPeriodEnd: true)));

        var dto = await _checkout.CancelAsync("sub_existing", Payer, CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(dto.CancelAtPeriodEnd, Is.True);
            Assert.That(dto.Status, Is.EqualTo("active"));
            Assert.That(dto.CurrentPeriodEnd, Is.Not.Null);
        });
    }

    [Test]
    public async Task A_guild_manager_who_is_not_the_payer_cannot_cancel()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan);

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.CancelAsync("sub_existing", Manager, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotThePayer));
            Assert.That(refusal.StatusCode, Is.EqualTo(403));
        });
    }

    [Test]
    public async Task Resuming_clears_the_flag()
    {
        var plan = await SeedPlanAsync();
        var subscription = await SeedSubscriptionAsync(plan);
        subscription.CancelAtPeriodEnd = true;
        await _db.SaveChangesAsync();

        _gateway.SetCancelAtPeriodEndAsync(
                "sub_existing", false, Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Snapshot("sub_existing", "active")));

        var dto = await _checkout.ResumeAsync("sub_existing", Payer, CancellationToken.None);

        Assert.That(dto.CancelAtPeriodEnd, Is.False);
    }

    /// <summary>Once it has actually ended there is no flag left to clear, and saying so is more use
    /// than a Stripe error about a canceled subscription.</summary>
    [Test]
    public async Task Resuming_something_that_has_already_lapsed_is_refused()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.Canceled);

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.ResumeAsync("sub_existing", Payer, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.SubscriptionLapsed));
            Assert.That(refusal.StatusCode, Is.EqualTo(409));
            Assert.That(
                _gateway.ReceivedCalls().Any(
                    call => call.GetMethodInfo().Name == nameof(IStripeGateway.SetCancelAtPeriodEndAsync)),
                Is.False);
        });
    }

    /// <summary>Past due is live.</summary>
    [Test]
    public async Task A_past_due_subscription_is_still_resumable()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.PastDue);

        _gateway.SetCancelAtPeriodEndAsync(
                "sub_existing", false, Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Snapshot("sub_existing", "past_due")));

        var dto = await _checkout.ResumeAsync("sub_existing", Payer, CancellationToken.None);

        Assert.That(dto.Status, Is.EqualTo("past_due"));
    }

    // ── Changing plan ────────────────────────────────────────────────────────

    [Test]
    public async Task Previewing_a_change_returns_the_proration_including_a_credit()
    {
        var plan = await SeedPlanAsync();
        await SeedPlanAsync("plus", 900, stripePriceId: "price_plus");
        await SeedSubscriptionAsync(plan);

        _gateway.PreviewSubscriptionPriceChangeAsync(
                "sub_existing", "price_plus", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StripeProrationPreview(
                -1450, "usd", 900, new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
                [new StripeProrationLine("Unused time on Pro", -1450),
                 new StripeProrationLine("Remaining time on Plus", 450)])));

        var preview = await _checkout.PreviewChangeAsync("sub_existing", "plus", Payer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(preview.ImmediateChargeMinorUnits, Is.EqualTo(-1450));
            Assert.That(preview.NextInvoiceTotalMinorUnits, Is.EqualTo(900));
            Assert.That(preview.Lines, Has.Count.EqualTo(2));
            Assert.That(preview.Lines[0].AmountMinorUnits, Is.Negative);
        });
    }

    [Test]
    public async Task Changing_plan_moves_the_pinned_version_with_the_price()
    {
        var plan = await SeedPlanAsync();
        var plus = await SeedPlanAsync("plus", 900, stripePriceId: "price_plus");
        await SeedSubscriptionAsync(plan);

        _gateway.ChangeSubscriptionPriceAsync(
                "sub_existing", "price_plus", Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Snapshot("sub_existing", "active")));

        var dto = await _checkout.ChangeAsync("sub_existing", "plus", Payer, CancellationToken.None);
        await _db.SaveChangesAsync();

        var row = await _db.Subscriptions.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(dto.PlanName, Is.EqualTo("plus"));
            Assert.That(dto.PriceMinorUnits, Is.EqualTo(900));
            Assert.That(row.PlanId, Is.EqualTo(plus.Id));
        });
    }

    [Test]
    public async Task Changing_to_a_plan_for_the_other_kind_of_subject_is_refused()
    {
        var plan = await SeedPlanAsync();
        await SeedPlanAsync("venta_plus", 600, "{\"user.max_devices\":\"10\"}", "price_vp");
        await SeedSubscriptionAsync(plan);

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _checkout.ChangeAsync(
                "sub_existing", "venta_plus", Payer, CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.NotPurchasable));
    }

    // ── Payment methods ──────────────────────────────────────────────────────

    private async Task<StripeCustomer> SeedCustomerAsync()
    {
        var customer = new StripeCustomer
        {
            Id = StripeCustomer.GenerateId(),
            UserId = Payer,
            StripeCustomerId = "cus_test",
        };

        _db.StripeCustomers.Add(customer);
        await _db.SaveChangesAsync();

        return customer;
    }

    private void HasCards(params string[] ids) =>
        _gateway.ListPaymentMethodsAsync("cus_test", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<StripePaymentMethodSummary>>(
                ids.Select((id, index) =>
                    new StripePaymentMethodSummary(id, "visa", "4242", 12, 2030, index == 0)).ToList()));

    [Test]
    public async Task An_account_that_has_never_paid_has_no_cards_rather_than_an_error()
    {
        var cards = await _cards.ListAsync(Payer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(cards, Is.Empty);
            Assert.That(_gateway.ReceivedCalls(), Is.Empty);
        });
    }

    [Test]
    public async Task Cards_are_read_from_Stripe_on_every_request()
    {
        await SeedCustomerAsync();
        HasCards("pm_one", "pm_two");

        var cards = await _cards.ListAsync(Payer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(cards, Has.Count.EqualTo(2));
            Assert.That(cards[0].IsDefault, Is.True);
            Assert.That(cards[0].Last4, Is.EqualTo("4242"));
        });
    }

    /// <summary>
    /// Stripe would accept this detach and then fail the next invoice a month later, to somebody
    /// who has long since forgotten pressing the button.
    /// </summary>
    [Test]
    public async Task Detaching_the_last_card_under_a_live_subscription_is_refused()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan);
        await SeedCustomerAsync();
        HasCards("pm_only");

        var refusal = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _cards.DetachAsync(Payer, "pm_only", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(BillingErrorCodes.LastPaymentMethod));
            Assert.That(refusal.StatusCode, Is.EqualTo(409));
            Assert.That(
                _gateway.ReceivedCalls().Any(
                    call => call.GetMethodInfo().Name == nameof(IStripeGateway.DetachPaymentMethodAsync)),
                Is.False);
        });
    }

    [Test]
    public async Task Detaching_one_of_two_cards_is_allowed_under_a_live_subscription()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan);
        await SeedCustomerAsync();
        HasCards("pm_one", "pm_two");

        await _cards.DetachAsync(Payer, "pm_two", CancellationToken.None);

        await _gateway.Received(1).DetachPaymentMethodAsync(
            "pm_two", Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Detaching_the_last_card_with_no_live_subscription_is_allowed()
    {
        var plan = await SeedPlanAsync();
        await SeedSubscriptionAsync(plan, SubscriptionStatus.Canceled);
        await SeedCustomerAsync();
        HasCards("pm_only");

        await _cards.DetachAsync(Payer, "pm_only", CancellationToken.None);

        await _gateway.Received(1).DetachPaymentMethodAsync(
            "pm_only", Arg.Any<StripeIdempotencyKey>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A payment method id is guessable in shape, and Stripe would happily detach somebody
    /// else's card for us. A card that is not theirs answers exactly as one that does not exist.
    /// </summary>
    [Test]
    public async Task A_card_that_is_not_on_this_account_cannot_be_detached_or_defaulted()
    {
        await SeedCustomerAsync();
        HasCards("pm_mine");

        var detach = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _cards.DetachAsync(Payer, "pm_somebody_elses", CancellationToken.None));

        var setDefault = Assert.ThrowsAsync<CheckoutRefusedException>(
            async () => await _cards.SetDefaultAsync(Payer, "pm_somebody_elses", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(detach!.Code, Is.EqualTo(BillingErrorCodes.UnknownPaymentMethod));
            Assert.That(detach.StatusCode, Is.EqualTo(404));
            Assert.That(setDefault!.Code, Is.EqualTo(BillingErrorCodes.UnknownPaymentMethod));
        });
    }

    [Test]
    public async Task Invoices_come_from_Stripe_with_both_links()
    {
        await SeedCustomerAsync();

        _gateway.ListInvoicesAsync("cus_test", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<StripeInvoiceSummary>>(
            [
                new StripeInvoiceSummary("in_1", "VENTA-0001", "paid", 2900, "usd",
                    new DateTimeOffset(2026, 8, 14, 10, 12, 0, TimeSpan.Zero),
                    "https://invoice.stripe.com/x", "https://pay.stripe.com/x/pdf"),
            ]));

        var invoices = await _cards.ListInvoicesAsync(Payer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(invoices.Single().Number, Is.EqualTo("VENTA-0001"));
            Assert.That(invoices.Single().HostedInvoiceUrl, Is.EqualTo("https://invoice.stripe.com/x"));
            Assert.That(invoices.Single().InvoicePdfUrl, Is.EqualTo("https://pay.stripe.com/x/pdf"));
        });
    }
}
