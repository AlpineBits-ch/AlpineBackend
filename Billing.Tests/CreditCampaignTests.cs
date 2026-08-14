using Billing.Application.Credit;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>Campaign budgets (monetization.md section 8.6), against a real Postgres.</summary>
[TestFixture]
public class CreditCampaignTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private CreditLedgerService _ledger = null!;
    private CreditCampaignService _campaigns = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        _clock = new TestClock(Now);
        _ledger = CreditFixtures.Ledger(_db, _clock);
        _campaigns = CreditFixtures.Campaigns(_db, _clock);
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    private async Task<CreditCampaign> OpenAsync(
        long budget = 1_000, long? perUser = null, int threshold = 80, bool automated = true)
    {
        var campaign = await _campaigns.CreateAsync(
            new CreateCreditCampaign(
                "incident-2026-08", "Compensation for the outage on the 3rd.", budget,
                MaxPerUserPoints: perUser, AlertThresholdPercent: threshold, Automated: automated),
            CreditFixtures.Staff,
            CancellationToken.None);

        await _db.SaveChangesAsync();
        return campaign;
    }

    private Task<CreditLedgerResult> IssueAsync(string user, long amount, string key) =>
        _ledger.IssueAsync(
            new IssueCredit(user, amount, "Outage compensation.", key, "incident-2026-08"),
            CancellationToken.None);

    [Test]
    public async Task An_issuance_against_a_campaign_charges_its_budget()
    {
        await OpenAsync(budget: 1_000);

        await IssueAsync("user_a", 300, "issue-a");
        await IssueAsync("user_b", 200, "issue-b");

        var campaign = await _db.CreditCampaigns.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(campaign.IssuedPoints, Is.EqualTo(500));
            Assert.That(campaign.RemainingPoints, Is.EqualTo(500));
        });
    }

    /// <summary>The refusal that makes the cap real, and the one a loop bug hits on its
    /// five-hundredth iteration rather than its five-thousandth.</summary>
    [Test]
    public async Task An_issuance_that_would_break_the_budget_is_refused_and_credits_nobody()
    {
        await OpenAsync(budget: 1_000);

        await IssueAsync("user_a", 900, "issue-a");

        Assert.That(async () => await IssueAsync("user_b", 200, "issue-b"),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.CampaignBudgetExhausted));

        Assert.Multiple(async () =>
        {
            Assert.That(await _ledger.BalanceAsync("user_b", CancellationToken.None), Is.Zero);
            Assert.That((await _db.CreditCampaigns.SingleAsync()).IssuedPoints, Is.EqualTo(900));
        });
    }

    /// <summary>A refused issuance must not consume budget either.</summary>
    [Test]
    public async Task A_refused_issuance_does_not_consume_budget()
    {
        await OpenAsync(budget: 1_000);

        // Past the wallet cap rather than past the campaign budget, so the refusal comes from the
        // other check and the campaign is the thing being observed.
        var ledger = CreditFixtures.Ledger(_db, _clock, CreditFixtures.Options(walletCap: 100));

        Assert.That(
            async () => await ledger.IssueAsync(
                new IssueCredit("user_a", 500, "Outage.", "issue-a", "incident-2026-08"),
                CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>());

        _db.ChangeTracker.Clear();

        Assert.That((await _db.CreditCampaigns.SingleAsync()).IssuedPoints, Is.Zero);
    }

    /// <summary>The alert fires before the cap, so somebody hears about it while there is still
    /// something to do. Once, not per issuance.</summary>
    [Test]
    public async Task Crossing_the_alert_threshold_stamps_the_campaign_once()
    {
        await OpenAsync(budget: 1_000, threshold: 80);

        await IssueAsync("user_a", 700, "issue-a");
        Assert.That((await _db.CreditCampaigns.SingleAsync()).AlertedAt, Is.Null);

        _db.ChangeTracker.Clear();
        await IssueAsync("user_b", 150, "issue-b");
        var alertedAt = (await _db.CreditCampaigns.SingleAsync()).AlertedAt;

        _db.ChangeTracker.Clear();
        _clock.Advance(TimeSpan.FromHours(1));
        await IssueAsync("user_c", 100, "issue-c");

        Assert.Multiple(async () =>
        {
            Assert.That(alertedAt, Is.EqualTo(Now));
            Assert.That((await _db.CreditCampaigns.SingleAsync()).AlertedAt, Is.EqualTo(Now),
                "the second crossing does not re-stamp");
        });
    }

    [Test]
    public async Task A_per_account_cap_stops_one_account_taking_the_whole_campaign()
    {
        await OpenAsync(budget: 1_000, perUser: 200);

        await IssueAsync("user_a", 200, "issue-a1");

        Assert.That(async () => await IssueAsync("user_a", 100, "issue-a2"),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.CampaignPerUserCapReached));

        // Somebody else is unaffected, which is the point of a per-account cap rather than a lower
        // total.
        await IssueAsync("user_b", 200, "issue-b1");

        Assert.That(await _ledger.BalanceAsync("user_b", CancellationToken.None), Is.EqualTo(200));
    }

    [Test]
    public async Task A_paused_campaign_issues_nothing()
    {
        await OpenAsync();
        await _campaigns.PauseAsync("incident-2026-08", CreditFixtures.Staff, CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.That(async () => await IssueAsync("user_a", 100, "issue-a"),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.CampaignClosed));
    }

    [Test]
    public async Task A_campaign_outside_its_window_issues_nothing()
    {
        var campaign = await _campaigns.CreateAsync(
            new CreateCreditCampaign(
                "incident-2026-08", "Old.", 1_000, EndsAt: Now.AddDays(-1)),
            CreditFixtures.Staff,
            CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(campaign.IsOpenAt(Now), Is.False);
            Assert.That(async () => await IssueAsync("user_a", 100, "issue-a"),
                Throws.InstanceOf<CreditRefusedException>());
        });
    }

    [Test]
    public void A_campaign_cannot_be_opened_without_a_budget()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await _campaigns.CreateAsync(
                    new CreateCreditCampaign("x", "No cap.", 0), CreditFixtures.Staff,
                    CancellationToken.None),
                Throws.InstanceOf<CreditRefusedException>()
                    .With.Property(nameof(CreditRefusedException.Code))
                    .EqualTo(CreditCampaignErrorCodes.BudgetRequired));

            Assert.That(
                async () => await _campaigns.CreateAsync(
                    new CreateCreditCampaign("x", "Negative cap.", -1), CreditFixtures.Staff,
                    CancellationToken.None),
                Throws.InstanceOf<CreditRefusedException>());
        });
    }

    [Test]
    public async Task Issuing_against_an_unknown_campaign_is_refused()
    {
        Assert.That(
            async () => await _ledger.IssueAsync(
                new IssueCredit("user_a", 100, "Outage.", "issue-a", "no-such-campaign"),
                CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditErrorCodes.UnknownCampaign));

        Assert.That(await _db.CreditEntries.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Raising_a_budget_reopens_issuance_and_rearms_the_alert()
    {
        await OpenAsync(budget: 1_000, threshold: 80);
        await IssueAsync("user_a", 900, "issue-a");

        Assert.That((await _db.CreditCampaigns.SingleAsync()).AlertedAt, Is.Not.Null);

        _db.ChangeTracker.Clear();
        await _campaigns.SetBudgetAsync("incident-2026-08", 5_000, CancellationToken.None);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await IssueAsync("user_b", 500, "issue-b");

        var campaign = await _db.CreditCampaigns.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(campaign.IssuedPoints, Is.EqualTo(1_400));
            Assert.That(campaign.AlertedAt, Is.Null, "back under the threshold, so it can alert again");
        });
    }

    [Test]
    public async Task A_budget_cannot_be_lowered_below_what_has_already_gone_out()
    {
        await OpenAsync(budget: 1_000);
        await IssueAsync("user_a", 600, "issue-a");
        _db.ChangeTracker.Clear();

        Assert.That(
            async () => await _campaigns.SetBudgetAsync("incident-2026-08", 500, CancellationToken.None),
            Throws.InstanceOf<CreditRefusedException>()
                .With.Property(nameof(CreditRefusedException.Code))
                .EqualTo(CreditCampaignErrorCodes.BudgetBelowIssued));
    }

    /// <summary>
    /// The database's own backstop, which is what survives the code path nobody has written yet.
    /// </summary>
    [Test]
    public async Task The_database_refuses_a_campaign_issued_past_its_budget()
    {
        await OpenAsync(budget: 1_000);

        Assert.That(
            async () => await _db.CreditCampaigns
                .Where(campaign => campaign.Code == "incident-2026-08")
                .ExecuteUpdateAsync(update => update.SetProperty(c => c.IssuedPoints, 1_001)),
            Throws.InstanceOf<DbUpdateException>().Or.InstanceOf<Npgsql.PostgresException>());
    }
}
