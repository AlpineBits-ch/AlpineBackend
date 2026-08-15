using Billing.Application.Promotions;
using Billing.Domain.Aggregates;
using Billing.Domain.Promotions;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>The eligibility rule catalogue, and the service that consults it.</summary>
[TestFixture]
public class PromotionEligibilityRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static PromotionEligibilityContext Context(int minimumAccountAgeDays = 0) =>
        new(Now, minimumAccountAgeDays);

    private static PromotionSignals Clean(
        bool emailVerified = true,
        bool phoneOnFile = true,
        DateTimeOffset? createdAt = null,
        int deviceCount = 1,
        bool priorSubscription = false,
        bool hasCard = true) =>
        new(emailVerified, phoneOnFile, createdAt ?? Now.AddYears(-1), deviceCount, priorSubscription,
            hasCard);

    // ── The catalogue ────────────────────────────────────────────────────────

    [Test]
    public void A_campaign_requiring_nothing_is_satisfied_by_anything()
    {
        var failed = PromotionEligibilityMap.Unsatisfied(
            PromotionEligibility.None,
            Clean(emailVerified: false, phoneOnFile: false, deviceCount: 0, hasCard: false),
            Context());

        Assert.That(failed, Is.Empty);
    }

    [Test]
    public void Every_rule_reports_itself_when_its_signal_is_missing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Codes(PromotionEligibility.VerifiedEmail, Clean(emailVerified: false)),
                Is.EquivalentTo(new[] { "verified_email" }));

            Assert.That(Codes(PromotionEligibility.PhoneNumberOnFile, Clean(phoneOnFile: false)),
                Is.EquivalentTo(new[] { "phone_number_on_file" }));

            Assert.That(Codes(PromotionEligibility.RegisteredDevice, Clean(deviceCount: 0)),
                Is.EquivalentTo(new[] { "registered_device" }));

            Assert.That(Codes(PromotionEligibility.NoPriorSubscription, Clean(priorSubscription: true)),
                Is.EquivalentTo(new[] { "no_prior_subscription" }));

            Assert.That(Codes(PromotionEligibility.PaymentCard, Clean(hasCard: false)),
                Is.EquivalentTo(new[] { "payment_card" }));
        });
    }

    [Test]
    public void Account_age_is_measured_against_the_campaigns_own_threshold()
    {
        var week = PromotionEligibility.MinimumAccountAge;

        Assert.Multiple(() =>
        {
            Assert.That(
                PromotionEligibilityMap.Unsatisfied(
                    week, Clean(createdAt: Now.AddDays(-3)), Context(minimumAccountAgeDays: 7)),
                Is.Not.Empty, "three days old against a seven day floor");

            Assert.That(
                PromotionEligibilityMap.Unsatisfied(
                    week, Clean(createdAt: Now.AddDays(-8)), Context(minimumAccountAgeDays: 7)),
                Is.Empty);
        });
    }

    /// <summary>An account whose creation date Identity could not give fails the age rule rather than
    /// passing it. A signal nobody could answer is not evidence of anything, and the refusal is the
    /// recoverable direction.</summary>
    [Test]
    public void An_unknown_creation_date_fails_the_age_rule()
    {
        // Constructed rather than taken from Clean, whose null means "use the default" - the whole
        // point here is a signal that is genuinely absent.
        var unknown = new PromotionSignals(
            EmailVerified: true,
            PhoneNumberOnFile: true,
            AccountCreatedAt: null,
            DeviceCount: 1,
            HasPriorSubscription: false,
            HasPaymentCard: true);

        var failed = PromotionEligibilityMap.Unsatisfied(
            PromotionEligibility.MinimumAccountAge, unknown, Context(minimumAccountAgeDays: 7));

        Assert.That(failed, Is.Not.Empty);
    }

    [Test]
    public void Several_missing_signals_are_all_reported()
    {
        var required = PromotionEligibility.VerifiedEmail
                       | PromotionEligibility.PaymentCard
                       | PromotionEligibility.RegisteredDevice;

        var failed = Codes(required, Clean(emailVerified: false, hasCard: false, deviceCount: 0));

        Assert.That(failed, Is.EquivalentTo(
            new[] { "verified_email", "registered_device", "payment_card" }));
    }

    /// <summary>The enum and the table are the two halves of one thing, and a member added to one and
    /// not the other is a control that silently does nothing.</summary>
    [Test]
    public void Every_signal_in_the_enum_has_a_rule_in_the_table()
    {
        var missing = Enum.GetValues<PromotionEligibility>()
            .Where(signal => signal != PromotionEligibility.None)
            .Where(signal => PromotionEligibilityMap.All.All(rule => rule.Signal != signal))
            .ToList();

        Assert.That(missing, Is.Empty);
    }

    /// <summary><b>Deliberate.</b> There is no verified phone on this platform, and a rule claiming
    /// otherwise would be trusted by whoever added it. If phone verification is ever built, this test
    /// is the place the decision gets revisited on purpose.</summary>
    [Test]
    public void There_is_no_verified_phone_rule()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                PromotionEligibilityMap.All.Select(rule => rule.Code),
                Has.No.Member("verified_phone"));

            Assert.That(
                PromotionEligibilityMap.All.Any(
                    rule => rule.Refusal.Contains("verified", StringComparison.OrdinalIgnoreCase)
                            && rule.Code.Contains("phone", StringComparison.Ordinal)),
                Is.False,
                "and no rule about a phone describes it as verified");
        });
    }

    [Test]
    public void Rule_codes_round_trip_through_the_parser()
    {
        var mask = PromotionEligibilityMap.Parse(["payment_card", "verified_email"]);

        Assert.Multiple(() =>
        {
            Assert.That(mask, Is.EqualTo(
                PromotionEligibility.PaymentCard | PromotionEligibility.VerifiedEmail));
            Assert.That(PromotionEligibilityMap.Names(mask),
                Is.EquivalentTo(new[] { "verified_email", "payment_card" }));
        });
    }

    [Test]
    public void An_unknown_rule_code_is_refused_rather_than_ignored()
    {
        Assert.That(
            () => PromotionEligibilityMap.Parse(["verified_email", "wishful_thinking"]),
            Throws.InstanceOf<ArgumentException>());
    }

    private static IReadOnlyList<string> Codes(
        PromotionEligibility required, PromotionSignals signals, int minimumAccountAgeDays = 0) =>
        [.. PromotionEligibilityMap
            .Unsatisfied(required, signals, Context(minimumAccountAgeDays))
            .Select(rule => rule.Code)];

    // ── The service that consults it ─────────────────────────────────────────

    [TestFixture]
    public class AgainstAnAccount
    {
        private MicroserviceContext _db = null!;
        private TestClock _clock = null!;
        private PromotionCampaignService _campaigns = null!;

        [OneTimeSetUp]
        public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

        [SetUp]
        public async Task Reset()
        {
            await PostgresTestDatabase.ResetToEmptyAsync();

            _db = PostgresTestDatabase.CreateContext();
            await _db.Database.MigrateAsync();

            _clock = new TestClock(Now);
            _campaigns = PromotionFixtures.Campaigns(_db, _clock);
        }

        [TearDown]
        public async Task Dispose() => await _db.DisposeAsync();

        private async Task<PromotionCampaign> OpenAsync(
            IReadOnlyList<string>? rules = null, int minimumAccountAgeDays = 0)
        {
            var campaign = await _campaigns.CreateAsync(
                PromotionFixtures.Open(rules: rules, minimumAccountAgeDays: minimumAccountAgeDays),
                PromotionFixtures.Staff,
                CancellationToken.None);

            await _db.SaveChangesAsync();
            return campaign;
        }

        [Test]
        public async Task An_unverified_email_fails_a_campaign_that_asks_for_one()
        {
            var campaign = await OpenAsync(rules: ["verified_email"]);

            var decision = await PromotionFixtures
                .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying(emailVerified: false))
                .EvaluateAsync(campaign, PromotionFixtures.Owner, PromotionFixtures.Guild, null,
                    CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Allowed, Is.False);
                Assert.That(decision.Code, Is.EqualTo(PromotionErrorCodes.NotEligible));
                Assert.That(decision.FailedRules, Is.EquivalentTo(new[] { "verified_email" }));
            });
        }

        /// <summary>Fails closed on absent evidence, and opens on evidence.</summary>
        [Test]
        public async Task A_campaign_requiring_a_card_refuses_without_evidence_and_admits_with_it()
        {
            var campaign = await OpenAsync(rules: ["payment_card"]);

            var service = PromotionFixtures.Eligibility(
                _db, _clock, PromotionFixtures.IdentitySaying());

            var unknown = await service.EvaluateAsync(
                campaign, PromotionFixtures.Owner, PromotionFixtures.Guild, null,
                CancellationToken.None);

            var withCard = await service.EvaluateAsync(
                campaign,
                PromotionFixtures.Owner,
                PromotionFixtures.Guild,
                new PromotionCardEvidence(HasCard: true, Fingerprint: "fp_a_card_of_their_own"),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(unknown.Allowed, Is.False);
                Assert.That(unknown.FailedRules, Is.EquivalentTo(new[] { "payment_card" }));

                Assert.That(withCard.Allowed, Is.True, withCard.Message);
                Assert.That(withCard.Hashes.CardHash, Is.Not.Null,
                    "the card that admitted them is what will refuse the next account presenting it");
            });
        }

        /// <summary>The "never paid" rule is answered from Billing's own rows rather than asked of
        /// anybody, because it is Billing's own question.</summary>
        [Test]
        public async Task A_prior_subscription_fails_a_never_paid_campaign()
        {
            var campaign = await OpenAsync(rules: ["no_prior_subscription"]);
            await SeedSubscriptionAsync(SubscriptionStatus.Canceled);

            var decision = await PromotionFixtures
                .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying())
                .EvaluateAsync(campaign, PromotionFixtures.Owner, PromotionFixtures.Guild, null,
                    CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Allowed, Is.False);
                Assert.That(decision.FailedRules, Is.EquivalentTo(new[] { "no_prior_subscription" }));
            });
        }

        /// <summary>An abandoned checkout is not a subscription.</summary>
        [Test]
        public async Task An_abandoned_checkout_is_not_a_prior_subscription()
        {
            var campaign = await OpenAsync(rules: ["no_prior_subscription"]);
            await SeedSubscriptionAsync(SubscriptionStatus.Incomplete);

            var decision = await PromotionFixtures
                .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying())
                .EvaluateAsync(campaign, PromotionFixtures.Owner, PromotionFixtures.Guild, null,
                    CancellationToken.None);

            Assert.That(decision.Allowed, Is.True, decision.Message);
        }

        /// <summary><b>Fails closed.</b> The opposite of the entitlement resolver's rule, deliberately:
        /// failing open there keeps somebody's voice chat working, and failing open here hands out
        /// plans for as long as the outage lasts.</summary>
        [Test]
        public async Task An_identity_outage_is_a_refusal_rather_than_a_pass()
        {
            var campaign = await OpenAsync();

            var decision = await PromotionFixtures
                .Eligibility(_db, _clock, PromotionFixtures.IdentityDown())
                .EvaluateAsync(campaign, PromotionFixtures.Owner, PromotionFixtures.Guild, null,
                    CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Allowed, Is.False);
                Assert.That(decision.Code, Is.EqualTo(PromotionErrorCodes.SignalsUnavailable));
            });
        }

        [Test]
        public async Task An_account_identity_does_not_know_about_is_refused()
        {
            var campaign = await OpenAsync();

            var decision = await PromotionFixtures
                .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying(found: false))
                .EvaluateAsync(campaign, "user_nobody", PromotionFixtures.Guild, null,
                    CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Allowed, Is.False);
                Assert.That(decision.Code, Is.EqualTo(PromotionErrorCodes.NotEligible));
            });
        }

        [Test]
        public async Task A_banned_account_and_a_bot_are_both_refused()
        {
            var campaign = await OpenAsync();

            var banned = await PromotionFixtures
                .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying(status: "Banned"))
                .EvaluateAsync(campaign, PromotionFixtures.Owner, PromotionFixtures.Guild, null,
                    CancellationToken.None);

            var bot = await PromotionFixtures
                .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying(isBot: true))
                .EvaluateAsync(campaign, PromotionFixtures.OtherOwner, PromotionFixtures.OtherGuild, null,
                    CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(banned.Allowed, Is.False);
                Assert.That(bot.Allowed, Is.False);
            });
        }

        /// <summary>A guild campaign needs a guild.</summary>
        [Test]
        public async Task A_guild_campaign_with_no_guild_named_is_refused()
        {
            var campaign = await OpenAsync();

            var decision = await PromotionFixtures
                .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying())
                .EvaluateAsync(campaign, PromotionFixtures.Owner, null, null, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Allowed, Is.False);
                Assert.That(decision.Code, Is.EqualTo(PromotionErrorCodes.TargetRequired));
            });
        }

        /// <summary>The preflight the client uses to decide whether to draw the button at all. Same
        /// decision, no sentence.</summary>
        [Test]
        public async Task The_preflight_agrees_with_the_full_evaluation()
        {
            var campaign = await OpenAsync();

            var service = PromotionFixtures.Eligibility(
                _db, _clock, PromotionFixtures.IdentitySaying());

            var before = await service.IsEligibleAsync(
                campaign.Code, PromotionFixtures.Owner, PromotionFixtures.Guild, CancellationToken.None);

            PromotionFixtures.Seed(
                _db, campaign, SubjectKind.User, PromotionFixtures.Owner, PromotionFixtures.Owner, Now);
            await _db.SaveChangesAsync();

            var after = await service.IsEligibleAsync(
                campaign.Code, PromotionFixtures.Owner, PromotionFixtures.Guild, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(before, Is.True);
                Assert.That(after, Is.False);
            });
        }

        [Test]
        public async Task The_preflight_says_no_for_a_campaign_that_does_not_exist()
        {
            await OpenAsync();

            var eligible = await PromotionFixtures
                .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying())
                .IsEligibleAsync(
                    "no-such-campaign", PromotionFixtures.Owner, PromotionFixtures.Guild,
                    CancellationToken.None);

            Assert.That(eligible, Is.False);
        }

        private async Task SeedSubscriptionAsync(SubscriptionStatus status)
        {
            var plan = new Plan
            {
                Id = Plan.GenerateId(),
                Name = "pro",
                DisplayName = "Pro",
                CurrentVersionNumber = 1,
                CreatedBy = "system",
                CreatedAt = Now,
                UpdatedAt = Now,
            };

            _db.Plans.Add(plan);

            _db.Subscriptions.Add(new Subscription
            {
                Id = Subscription.GenerateId(),
                StripeSubscriptionId = $"sub_{Guid.NewGuid():N}",
                PayerUserId = PromotionFixtures.Owner,
                SubjectKind = SubjectKind.Guild,
                SubjectId = "gild_paid_before",
                PlanId = plan.Id,
                VersionNumber = 1,
                Status = status,
                CreatedAt = Now,
                UpdatedAt = Now,
            });

            await _db.SaveChangesAsync();
        }
    }
}
