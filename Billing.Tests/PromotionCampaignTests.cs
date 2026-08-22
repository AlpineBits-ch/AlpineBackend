using Billing.Application.Promotions;
using Billing.Domain.Aggregates;
using Billing.Domain.Promotions;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>Campaign budgets, alerts and pauses, against a real Postgres.</summary>
[TestFixture]
public class PromotionCampaignTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private PromotionCampaignService _campaigns = null!;
    private PromotionRedemptionService _redemptions = null!;

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
        _redemptions = PromotionFixtures.Redemptions(_db, _clock, _campaigns);
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    private async Task<PromotionCampaign> OpenAsync(
        long budget = 100, int threshold = 80, DateTimeOffset? endsAt = null)
    {
        var campaign = await _campaigns.CreateAsync(
            PromotionFixtures.Open(budget: budget, alertThresholdPercent: threshold, endsAt: endsAt),
            PromotionFixtures.Staff,
            CancellationToken.None);

        await _db.SaveChangesAsync();
        return campaign;
    }

    /// <summary>
    /// Charge then record, the order TrialService uses: the slot is taken before anything is
    /// created, so a closed or exhausted campaign refuses before a Stripe subscription exists.
    /// </summary>
    private async Task RedeemAsync(PromotionCampaign campaign, string owner, string guild)
    {
        _campaigns.Charge(campaign, null);

        await _redemptions.RecordAsync(
            campaign, owner, guild, PromotionIdentityHashes.None, null, CancellationToken.None);

        await _db.SaveChangesAsync();
    }

    // ── The budget ───────────────────────────────────────────────────────────

    [Test]
    public async Task A_redemption_charges_the_campaign_budget()
    {
        var campaign = await OpenAsync(budget: 10);

        await RedeemAsync(campaign, "user_a", "gild_a");
        await RedeemAsync(campaign, "user_b", "gild_b");

        var reread = await _db.PromotionCampaigns.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(reread.IssuedRedemptions, Is.EqualTo(2));
            Assert.That(reread.RemainingRedemptions, Is.EqualTo(8));
        });
    }

    /// <summary>The refusal that makes the cap real.</summary>
    [Test]
    public async Task A_campaign_cannot_issue_past_its_budget()
    {
        var campaign = await OpenAsync(budget: 1);

        await RedeemAsync(campaign, "user_a", "gild_a");

        Assert.That(
            async () => await RedeemAsync(campaign, "user_b", "gild_b"),
            Throws.InstanceOf<PromotionRefusedException>()
                .With.Property(nameof(PromotionRefusedException.Code))
                .EqualTo(PromotionErrorCodes.CampaignBudgetExhausted));

        _db.ChangeTracker.Clear();

        Assert.Multiple(async () =>
        {
            Assert.That((await _db.PromotionCampaigns.SingleAsync()).IssuedRedemptions, Is.EqualTo(1));
            Assert.That(await _db.PromotionRedemptions.CountAsync(), Is.EqualTo(2),
                "the owner and guild rows of the one redemption that succeeded, and nothing else");
        });
    }

    /// <summary>Eligibility refuses an exhausted campaign before it gets anywhere near Identity, so a
    /// client is told the offer has run out rather than being told nothing and failing at the last
    /// step.</summary>
    [Test]
    public async Task An_exhausted_campaign_is_refused_at_eligibility_too()
    {
        var campaign = await OpenAsync(budget: 1);
        await RedeemAsync(campaign, "user_a", "gild_a");
        _db.ChangeTracker.Clear();

        var reread = await _db.PromotionCampaigns.SingleAsync();

        var decision = await PromotionFixtures
            .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying())
            .EvaluateAsync(reread, "user_b", "gild_b", card: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Code, Is.EqualTo(PromotionErrorCodes.CampaignBudgetExhausted));
        });
    }

    /// <summary>The alert fires before the cap, so somebody hears about it while there is still
    /// something to do. Once, not per redemption.</summary>
    [Test]
    public async Task Crossing_the_alert_threshold_stamps_the_campaign_once()
    {
        var campaign = await OpenAsync(budget: 10, threshold: 80);

        for (var i = 0; i < 7; i++) await RedeemAsync(campaign, $"user_{i}", $"gild_{i}");

        _db.ChangeTracker.Clear();
        Assert.That((await _db.PromotionCampaigns.SingleAsync()).AlertedAt, Is.Null,
            "seven of ten is below the eighty percent threshold");

        _db.ChangeTracker.Clear();
        await RedeemAsync(await _db.PromotionCampaigns.SingleAsync(), "user_7", "gild_7");

        _db.ChangeTracker.Clear();
        var alertedAt = (await _db.PromotionCampaigns.SingleAsync()).AlertedAt;

        _db.ChangeTracker.Clear();
        _clock.Advance(TimeSpan.FromHours(1));
        await RedeemAsync(await _db.PromotionCampaigns.SingleAsync(), "user_8", "gild_8");

        _db.ChangeTracker.Clear();

        Assert.Multiple(async () =>
        {
            Assert.That(alertedAt, Is.EqualTo(Now));
            Assert.That((await _db.PromotionCampaigns.SingleAsync()).AlertedAt, Is.EqualTo(Now),
                "the second crossing does not re-stamp");
        });
    }

    [Test]
    public async Task Raising_a_budget_reopens_redemption_and_rearms_the_alert()
    {
        var campaign = await OpenAsync(budget: 10, threshold: 80);

        for (var i = 0; i < 9; i++)
        {
            _db.ChangeTracker.Clear();
            await RedeemAsync(await _db.PromotionCampaigns.SingleAsync(), $"user_{i}", $"gild_{i}");
        }

        _db.ChangeTracker.Clear();
        Assert.That((await _db.PromotionCampaigns.SingleAsync()).AlertedAt, Is.Not.Null);

        _db.ChangeTracker.Clear();
        await _campaigns.SetBudgetAsync(campaign.Code, 100, CancellationToken.None);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var reread = await _db.PromotionCampaigns.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(reread.RemainingRedemptions, Is.EqualTo(91));
            Assert.That(reread.AlertedAt, Is.Null, "back under the threshold, so it can alert again");
        });
    }

    [Test]
    public async Task A_budget_cannot_be_lowered_below_what_has_already_gone_out()
    {
        var campaign = await OpenAsync(budget: 10);
        await RedeemAsync(campaign, "user_a", "gild_a");
        _db.ChangeTracker.Clear();

        Assert.That(
            async () => await _campaigns.SetBudgetAsync(campaign.Code, 0, CancellationToken.None),
            Throws.InstanceOf<PromotionRefusedException>()
                .With.Property(nameof(PromotionRefusedException.Code))
                .EqualTo(PromotionErrorCodes.BudgetRequired));

        Assert.That(
            async () => await _campaigns.SetBudgetAsync(campaign.Code, -1, CancellationToken.None),
            Throws.InstanceOf<PromotionRefusedException>());
    }

    /// <summary>The database's own backstop, which survives the code path nobody has written yet.
    /// </summary>
    [Test]
    public async Task The_database_refuses_a_campaign_issued_past_its_budget()
    {
        await OpenAsync(budget: 10);

        Assert.That(
            async () => await _db.PromotionCampaigns
                .Where(campaign => campaign.Code == PromotionFixtures.Campaign)
                .ExecuteUpdateAsync(update => update.SetProperty(c => c.IssuedRedemptions, 11)),
            Throws.InstanceOf<DbUpdateException>().Or.InstanceOf<Npgsql.PostgresException>());
    }

    // ── Pause and window ─────────────────────────────────────────────────────

    [Test]
    public async Task A_paused_campaign_refuses()
    {
        var campaign = await OpenAsync();

        await _campaigns.PauseAsync(campaign.Code, PromotionFixtures.Staff, CancellationToken.None);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var reread = await _db.PromotionCampaigns.SingleAsync();

        var decision = await PromotionFixtures
            .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying())
            .EvaluateAsync(reread, "user_a", "gild_a", card: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(reread.IsPaused, Is.True);
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Code, Is.EqualTo(PromotionErrorCodes.CampaignClosed));
            Assert.That(
                async () => await RedeemAsync(reread, "user_a", "gild_a"),
                Throws.InstanceOf<PromotionRefusedException>()
                    .With.Property(nameof(PromotionRefusedException.Code))
                    .EqualTo(PromotionErrorCodes.CampaignClosed),
                "and the redemption path refuses it too, not only the preflight");
        });
    }

    [Test]
    public async Task Resuming_a_paused_campaign_lets_it_redeem_again()
    {
        var campaign = await OpenAsync();

        await _campaigns.PauseAsync(campaign.Code, PromotionFixtures.Staff, CancellationToken.None);
        await _db.SaveChangesAsync();

        await _campaigns.ResumeAsync(campaign.Code, CancellationToken.None);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await RedeemAsync(await _db.PromotionCampaigns.SingleAsync(), "user_a", "gild_a");

        _db.ChangeTracker.Clear();
        Assert.That((await _db.PromotionCampaigns.SingleAsync()).IssuedRedemptions, Is.EqualTo(1));
    }

    [Test]
    public async Task A_campaign_outside_its_window_refuses()
    {
        var campaign = await OpenAsync(endsAt: Now.AddDays(-1));

        Assert.Multiple(() =>
        {
            Assert.That(campaign.IsOpenAt(Now), Is.False);
            Assert.That(
                async () => await RedeemAsync(campaign, "user_a", "gild_a"),
                Throws.InstanceOf<PromotionRefusedException>()
                    .With.Property(nameof(PromotionRefusedException.Code))
                    .EqualTo(PromotionErrorCodes.CampaignClosed));
        });
    }

    // ── Creating one ─────────────────────────────────────────────────────────

    [Test]
    public void A_campaign_cannot_be_opened_without_a_budget()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await _campaigns.CreateAsync(
                    PromotionFixtures.Open(budget: 0), PromotionFixtures.Staff, CancellationToken.None),
                Throws.InstanceOf<PromotionRefusedException>()
                    .With.Property(nameof(PromotionRefusedException.Code))
                    .EqualTo(PromotionErrorCodes.BudgetRequired));

            Assert.That(
                async () => await _campaigns.CreateAsync(
                    PromotionFixtures.Open(budget: -1), PromotionFixtures.Staff, CancellationToken.None),
                Throws.InstanceOf<PromotionRefusedException>());
        });
    }

    /// <summary>Section 7.5: whatever is promised without an end date becomes permanent by default, so
    /// a trial with no length is a pricing decision made by accident.</summary>
    [Test]
    public void A_campaign_cannot_be_opened_without_a_trial_length()
    {
        Assert.That(
            async () => await _campaigns.CreateAsync(
                PromotionFixtures.Open(trialDays: 0), PromotionFixtures.Staff, CancellationToken.None),
            Throws.InstanceOf<PromotionRefusedException>()
                .With.Property(nameof(PromotionRefusedException.Code))
                .EqualTo(PromotionErrorCodes.TrialDaysRequired));
    }

    [Test]
    public async Task Two_campaigns_cannot_share_a_code()
    {
        await OpenAsync();

        Assert.That(
            async () => await _campaigns.CreateAsync(
                PromotionFixtures.Open(), PromotionFixtures.Staff, CancellationToken.None),
            Throws.InstanceOf<PromotionRefusedException>()
                .With.Property(nameof(PromotionRefusedException.Code))
                .EqualTo(PromotionErrorCodes.DuplicateCode));
    }

    /// <summary>An unknown rule is refused rather than dropped.</summary>
    [Test]
    public void A_campaign_naming_an_unknown_rule_is_refused()
    {
        Assert.That(
            async () => await _campaigns.CreateAsync(
                PromotionFixtures.Open(rules: ["verified_phone"]),
                PromotionFixtures.Staff,
                CancellationToken.None),
            Throws.InstanceOf<PromotionRefusedException>()
                .With.Property(nameof(PromotionRefusedException.Code))
                .EqualTo(PromotionErrorCodes.UnknownRule));
    }

    [Test]
    public async Task Required_rules_round_trip_through_the_database()
    {
        await _campaigns.CreateAsync(
            PromotionFixtures.Open(rules: ["verified_email", "payment_card", "minimum_account_age"]),
            PromotionFixtures.Staff,
            CancellationToken.None);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var reread = await _db.PromotionCampaigns.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(reread.RequiredSignals, Is.EqualTo(
                PromotionEligibility.VerifiedEmail
                | PromotionEligibility.PaymentCard
                | PromotionEligibility.MinimumAccountAge));

            Assert.That(reread.RequiresCard, Is.True);
            Assert.That(PromotionEligibilityMap.Names(reread.RequiredSignals),
                Is.EquivalentTo(new[] { "verified_email", "minimum_account_age", "payment_card" }));
        });
    }

    [Test]
    public async Task An_unknown_campaign_is_a_named_refusal_rather_than_a_null()
    {
        await OpenAsync();

        Assert.That(
            async () => await _campaigns.RequireAsync("no-such-campaign", CancellationToken.None),
            Throws.InstanceOf<PromotionRefusedException>()
                .With.Property(nameof(PromotionRefusedException.Code))
                .EqualTo(PromotionErrorCodes.UnknownCampaign));
    }

    /// <summary>A campaign is addressed by its code by the humans who run it and by its id by the rows
    /// that reference it, so both have to resolve.</summary>
    [Test]
    public async Task A_campaign_resolves_by_code_or_by_id()
    {
        var campaign = await OpenAsync();

        Assert.Multiple(async () =>
        {
            Assert.That((await _campaigns.FindAsync(campaign.Code, CancellationToken.None))?.Id,
                Is.EqualTo(campaign.Id));
            Assert.That((await _campaigns.FindAsync(campaign.Id, CancellationToken.None))?.Code,
                Is.EqualTo(campaign.Code));
        });
    }
}
