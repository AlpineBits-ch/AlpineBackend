using Billing.Application.Promotions;
using Billing.Application.Services;
using Billing.Application.Stripe;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Echo.Entitlements.Model;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Wolverine;

namespace Billing.Tests.Helpers;

/// <summary>
/// The pieces every promotion test needs: the services over a real Postgres, a substituted
/// Identity, and a salt.
/// </summary>
internal static class PromotionFixtures
{
    public const string Salt = "test-salt-not-a-real-one";
    public const string Campaign = "pro-trial-2026";
    public const string Staff = "user_promo_staff";
    public const string Owner = "user_promo_owner";
    public const string OtherOwner = "user_promo_other";
    public const string Guild = "gild_promo_target";
    public const string OtherGuild = "gild_promo_other";

    public static PromotionOptions Options(string? salt = null) =>
        new() { HashSalt = salt ?? Salt };

    public static PromotionHasher Hasher(string? salt = null) =>
        new(Microsoft.Extensions.Options.Options.Create(Options(salt)));

    public static PromotionCampaignService Campaigns(MicroserviceContext db, TimeProvider clock) =>
        new(db, clock);

    public static PromotionRedemptionService Redemptions(
        MicroserviceContext db,
        TimeProvider clock,
        PromotionCampaignService? campaigns = null,
        ILoggerFactory? loggers = null) =>
        new(db, campaigns ?? Campaigns(db, clock), clock,
            loggers?.CreateLogger<PromotionRedemptionService>());

    public static PromotionEligibilityService Eligibility(
        MicroserviceContext db,
        TimeProvider clock,
        IMessageBus bus,
        string? salt = null,
        ILoggerFactory? loggers = null) =>
        new(db, Hasher(salt), bus, clock, Microsoft.Extensions.Options.Options.Create(Options(salt)),
            loggers?.CreateLogger<PromotionEligibilityService>());

    // ── The trial path ───────────────────────────────────────────────────────

    public const string StripeSecretKey = "sk_test_promotiontests";
    public const string StripeCustomerId = "cus_promo";
    public const string StripePriceId = "price_pro";
    public const string PaymentMethodId = "pm_promo_card";
    public const string TrialSubscriptionId = "sub_promo_trial";

    /// <summary>
    /// A Stripe that behaves: it has a customer, it creates trialing subscriptions, and it answers
    /// with the card identity the test names.
    /// </summary>
    public static IStripeGateway StripeSaying(
        string? fingerprint = null,
        string paymentMethodId = PaymentMethodId,
        string subscriptionId = TrialSubscriptionId,
        string customerId = StripeCustomerId,
        string status = "trialing",
        string? clientSecret = "seti_promo_secret")
    {
        var gateway = Substitute.For<IStripeGateway>();

        // Distinct per account, because the customer table is unique in both directions: one Stripe
        // customer against two users would let one person's card pay under another's account, and a
        // fixture that handed out one id for everybody would hit that index rather than the behaviour
        // under test.
        gateway.CreateCustomerAsync(
                Arg.Any<StripeCustomerRequest>(), Arg.Any<StripeIdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StripeObjectRef(customerId)));

        gateway.GetCardIdentityAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                fingerprint is null ? null : new StripeCardIdentity(paymentMethodId, fingerprint)));

        gateway.CreateSubscriptionAsync(
                Arg.Any<StripeSubscriptionRequest>(), Arg.Any<StripeIdempotencyKey>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StripeSubscriptionResult(
                Snapshot(subscriptionId, status), clientSecret)));

        return gateway;
    }

    public static StripeSubscriptionSnapshot Snapshot(
        string id = TrialSubscriptionId, string status = "trialing") =>
        new(id, status, StripeCustomerId, StripePriceId,
            new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero), false, null,
            new Dictionary<string, string>());

    /// <summary>A Guild that says yes to <c>ManageGuild</c>.</summary>
    public static IMessageBus AllowingGuild(IMessageBus? bus = null, bool allowed = true)
    {
        var target = bus ?? Substitute.For<IMessageBus>();

        target.InvokeAsync<HasUserPermissionToGuildResponse>(
                Arg.Any<HasUserPermissionToGuildRequest>(), Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>())
            .Returns(new HasUserPermissionToGuildResponse
            {
                IsAllowed = allowed,
                Permission = ExternalPermission.ManageGuild,
            });

        return target;
    }

    /// <summary>The whole trial path, wired the way the container wires it.</summary>
    public static TrialService Trials(
        MicroserviceContext db,
        TimeProvider clock,
        IStripeGateway gateway,
        IMessageBus bus,
        PromotionCampaignService? campaigns = null,
        string? salt = null,
        ILoggerFactory? loggers = null)
    {
        var options = Plans.Options();
        var registry = new StripeCustomerRegistry(db, gateway);
        var checkout = new SubscriptionCheckoutService(db, gateway, registry, options, bus);
        var catalogue = new PlanCatalogueService(db, Plans.Catalogue());

        var planService = new PlanService(
            db, catalogue, new EntitlementVersionService(db), options, clock);

        campaigns ??= Campaigns(db, clock);

        return new TrialService(
            db,
            campaigns,
            Eligibility(db, clock, bus, salt, loggers),
            Redemptions(db, clock, campaigns, loggers),
            checkout,
            registry,
            planService,
            gateway,
            options,
            loggers?.CreateLogger<TrialService>());
    }

    /// <summary>The plans a trial needs to exist at all: the one it confers, priced and mirrored into
    /// Stripe, and the instance default a guild falls back to when a trial moves away from it.</summary>
    public static async Task SeedPlansAsync(MicroserviceContext db, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);

        Add(Plans.Pro, 2900, StripePriceId);
        Add(Plans.Free, null, null);

        await db.SaveChangesAsync();

        void Add(string name, long? price, string? stripePriceId)
        {
            var plan = new Plan
            {
                Id = Plan.GenerateId(),
                Name = name,
                DisplayName = name.ToUpperInvariant(),
                CurrentVersionNumber = 1,
                CreatedBy = "system",
                StripeProductId = "prod_promo",
                CreatedAt = now,
                UpdatedAt = now,
            };

            db.Plans.Add(plan);

            db.PlanVersions.Add(new PlanVersion
            {
                Id = PlanVersion.GenerateId(),
                PlanId = plan.Id,
                VersionNumber = 1,
                ValuesJson = "{\"voice.max_participants\":\"75\"}",
                PriceMinorUnits = price,
                Currency = price is null ? null : "usd",
                StripePriceId = stripePriceId,
                Reason = "Launch.",
                CreatedBy = "system",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    /// <summary>An Identity that answers with a clean, ordinary account.</summary>
    public static IMessageBus IdentitySaying(
        bool found = true,
        bool emailVerified = true,
        string? phoneNumber = "+41791112233",
        DateTimeOffset? createdAt = null,
        IEnumerable<string>? deviceIds = null,
        string status = "Active",
        bool isBot = false)
    {
        var bus = Substitute.For<IMessageBus>();

        bus.InvokeAsync<GetTrialEligibilitySignalsResponse>(
                Arg.Any<GetTrialEligibilitySignalsRequest>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>())
            .Returns(new GetTrialEligibilitySignalsResponse
            {
                Found = found,
                EmailVerified = emailVerified,
                PhoneNumber = phoneNumber,
                CreatedAt = createdAt ?? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                DeviceIds = [.. deviceIds ?? ["device-a"]],
                Status = status,
                IsBot = isBot,
            });

        return bus;
    }

    /// <summary>An Identity that is down.</summary>
    public static IMessageBus IdentityDown()
    {
        var bus = Substitute.For<IMessageBus>();

        bus.InvokeAsync<GetTrialEligibilitySignalsResponse>(
                Arg.Any<GetTrialEligibilitySignalsRequest>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>())
            .Returns<GetTrialEligibilitySignalsResponse>(_ =>
                throw new TimeoutException("Identity did not answer."));

        return bus;
    }

    public static CreatePromotionCampaign Open(
        string code = Campaign,
        long budget = 100,
        int trialDays = 30,
        IReadOnlyList<string>? rules = null,
        int minimumAccountAgeDays = 0,
        int alertThresholdPercent = 80,
        SubjectKind subjectKind = SubjectKind.Guild,
        DateTimeOffset? endsAt = null) =>
        new(code,
            "Thirty days of Pro for a new community.",
            Plans.Pro,
            trialDays,
            budget,
            subjectKind,
            rules,
            minimumAccountAgeDays,
            MaxPerSubject: 1,
            AlertThresholdPercent: alertThresholdPercent,
            StartsAt: null,
            EndsAt: endsAt);

    /// <summary>
    /// A redemption written by hand, the way it would look after a trial that this package cannot
    /// yet start.
    /// </summary>
    public static PromotionRedemption Seed(
        MicroserviceContext db,
        PromotionCampaign campaign,
        SubjectKind kind,
        string subjectId,
        string ownerUserId,
        DateTimeOffset redeemedAt,
        DateTimeOffset? endsAt = null,
        DateTimeOffset? expiredAt = null)
    {
        var row = new PromotionRedemption
        {
            Id = PromotionRedemption.GenerateId(),
            CampaignId = campaign.Id,
            SubjectKind = kind,
            SubjectId = subjectId,
            OwnerUserId = ownerUserId,
            RedeemedAt = redeemedAt,
            EndsAt = endsAt ?? redeemedAt.AddDays(campaign.TrialDays),
            ExpiredAt = expiredAt,
            CreatedAt = redeemedAt,
            UpdatedAt = redeemedAt,
        };

        db.PromotionRedemptions.Add(row);
        return row;
    }

    public static PromotionIdentityMark SeedMark(
        MicroserviceContext db,
        PromotionCampaign campaign,
        PromotionRedemption redemption,
        PromotionIdentityKind kind,
        string hash,
        DateTimeOffset at)
    {
        var mark = new PromotionIdentityMark
        {
            Id = PromotionIdentityMark.GenerateId(),
            CampaignId = campaign.Id,
            RedemptionId = redemption.Id,
            Kind = kind,
            Hash = hash,
            CreatedAt = at,
            UpdatedAt = at,
        };

        db.PromotionIdentityMarks.Add(mark);
        return mark;
    }
}
