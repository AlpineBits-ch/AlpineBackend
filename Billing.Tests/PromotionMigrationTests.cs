using Billing.Domain.Aggregates;
using Billing.Domain.Promotions;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>The promotion schema, against a real Postgres.</summary>
[TestFixture]
public class PromotionMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public Task Reset() => PostgresTestDatabase.ResetToEmptyAsync();

    [Test]
    public async Task The_migration_applies_to_an_empty_database_and_is_idempotent()
    {
        await using (var first = PostgresTestDatabase.CreateContext())
        {
            await first.Database.MigrateAsync();
            first.PromotionCampaigns.Add(Campaign());
            await first.SaveChangesAsync();
        }

        await using var second = PostgresTestDatabase.CreateContext();
        await second.Database.MigrateAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await second.Database.GetPendingMigrationsAsync(), Is.Empty);
            Assert.That(await second.PromotionCampaigns.CountAsync(), Is.EqualTo(1));
        });
    }

    /// <summary>The same guard the credit and grant tables carry.</summary>
    [Test]
    public async Task The_identity_kind_is_text_and_no_postgres_enum_type_exists()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var kind = await PostgresTestDatabase.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = 'promotion_identity_marks' AND column_name = 'kind'");

        var subjectKind = await PostgresTestDatabase.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = 'promotion_redemptions' AND column_name = 'subject_kind'");

        var enumTypes = await PostgresTestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace "
            + "WHERE t.typtype = 'e' AND n.nspname = 'public'");

        Assert.Multiple(() =>
        {
            Assert.That(kind, Is.EqualTo("text"));
            Assert.That(subjectKind, Is.EqualTo("text"));
            Assert.That(enumTypes, Is.Zero);
        });
    }

    /// <summary>The one that makes every farm case a constraint rather than a check.</summary>
    [Test]
    public async Task One_subject_cannot_redeem_one_campaign_twice()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var campaign = Campaign();
        db.PromotionCampaigns.Add(campaign);
        db.PromotionRedemptions.Add(Redemption(campaign, SubjectKind.User, PromotionFixtures.Owner));
        await db.SaveChangesAsync();

        db.PromotionRedemptions.Add(Redemption(campaign, SubjectKind.User, PromotionFixtures.Owner));

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    /// <summary>The index is over the kind as well as the id, so a guild and an account that happened
    /// to share an id string are still two subjects. Prefixed ids make that unlikely and the index
    /// makes it impossible to get wrong.</summary>
    [Test]
    public async Task A_user_and_a_guild_row_can_share_a_campaign()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var campaign = Campaign();
        db.PromotionCampaigns.Add(campaign);
        db.PromotionRedemptions.Add(Redemption(campaign, SubjectKind.User, "same-id"));
        db.PromotionRedemptions.Add(Redemption(campaign, SubjectKind.Guild, "same-id"));

        Assert.That(async () => await db.SaveChangesAsync(), Throws.Nothing);
    }

    [Test]
    public async Task Two_marks_cannot_share_a_campaign_kind_and_hash()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var campaign = Campaign();
        var redemption = Redemption(campaign, SubjectKind.User, PromotionFixtures.Owner);

        db.PromotionCampaigns.Add(campaign);
        db.PromotionRedemptions.Add(redemption);
        db.PromotionIdentityMarks.Add(Mark(campaign, redemption, PromotionIdentityKind.Device, "h"));
        await db.SaveChangesAsync();

        db.PromotionIdentityMarks.Add(Mark(campaign, redemption, PromotionIdentityKind.Device, "h"));

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    /// <summary>Marks are scoped to their campaign, so last year's offer is not a permanent exclusion
    /// list for this year's.</summary>
    [Test]
    public async Task One_hash_can_appear_under_two_campaigns()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var first = Campaign("trial-2025");
        var second = Campaign("trial-2026");
        var one = Redemption(first, SubjectKind.User, PromotionFixtures.Owner);
        var two = Redemption(second, SubjectKind.User, PromotionFixtures.Owner);

        db.PromotionCampaigns.AddRange(first, second);
        db.PromotionRedemptions.AddRange(one, two);
        db.PromotionIdentityMarks.Add(Mark(first, one, PromotionIdentityKind.Device, "h"));
        db.PromotionIdentityMarks.Add(Mark(second, two, PromotionIdentityKind.Device, "h"));

        Assert.That(async () => await db.SaveChangesAsync(), Throws.Nothing);
    }

    [Test]
    public async Task A_campaign_cannot_be_stored_over_its_budget()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var campaign = Campaign();
        campaign.TotalBudgetRedemptions = 5;
        campaign.IssuedRedemptions = 6;

        db.PromotionCampaigns.Add(campaign);

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    /// <summary>Zero is refused too: a campaign that can never redeem is a configuration mistake, not a
    /// paused campaign.</summary>
    [Test]
    public async Task A_campaign_cannot_be_stored_with_no_budget()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var campaign = Campaign();
        campaign.TotalBudgetRedemptions = 0;

        db.PromotionCampaigns.Add(campaign);

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    /// <summary>The required-signals bitmask survives the round trip as a number, which is what a
    /// flags enum should be on the wire to the database. The names are produced from it on the way out
    /// - see <c>PromotionEligibilityMap.Names</c>.</summary>
    [Test]
    public async Task The_required_signals_mask_round_trips()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var campaign = Campaign();
        campaign.RequiredSignals =
            PromotionEligibility.VerifiedEmail | PromotionEligibility.PaymentCard;

        db.PromotionCampaigns.Add(campaign);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.PromotionCampaigns.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored.RequiredSignals.HasFlag(PromotionEligibility.VerifiedEmail), Is.True);
            Assert.That(stored.RequiredSignals.HasFlag(PromotionEligibility.PaymentCard), Is.True);
            Assert.That(stored.RequiredSignals.HasFlag(PromotionEligibility.RegisteredDevice), Is.False);
        });
    }

    /// <summary>A campaign somebody has redeemed cannot be deleted out from under the record. The same
    /// Restrict every other reference in this context uses, and here it is what stops the audit trail
    /// being removable by deleting one row.</summary>
    [Test]
    public async Task A_campaign_with_redemptions_cannot_be_deleted()
    {
        await using var db = PostgresTestDatabase.CreateContext();
        await db.Database.MigrateAsync();

        var campaign = Campaign();
        db.PromotionCampaigns.Add(campaign);
        db.PromotionRedemptions.Add(Redemption(campaign, SubjectKind.User, PromotionFixtures.Owner));
        await db.SaveChangesAsync();

        db.PromotionCampaigns.Remove(campaign);

        Assert.That(async () => await db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    private static PromotionCampaign Campaign(string code = PromotionFixtures.Campaign) => new()
    {
        Id = PromotionCampaign.GenerateId(),
        Code = code,
        Description = "Thirty days of Pro.",
        Plan = Plans.Pro,
        TrialDays = 30,
        SubjectKind = SubjectKind.Guild,
        TotalBudgetRedemptions = 100,
        IssuedRedemptions = 0,
        CreatedBy = PromotionFixtures.Staff,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static PromotionRedemption Redemption(
        PromotionCampaign campaign, SubjectKind kind, string subjectId) => new()
    {
        Id = PromotionRedemption.GenerateId(),
        CampaignId = campaign.Id,
        SubjectKind = kind,
        SubjectId = subjectId,
        OwnerUserId = PromotionFixtures.Owner,
        RedeemedAt = Now,
        EndsAt = Now.AddDays(30),
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static PromotionIdentityMark Mark(
        PromotionCampaign campaign,
        PromotionRedemption redemption,
        PromotionIdentityKind kind,
        string hash) => new()
    {
        Id = PromotionIdentityMark.GenerateId(),
        CampaignId = campaign.Id,
        RedemptionId = redemption.Id,
        Kind = kind,
        Hash = hash,
        CreatedAt = Now,
        UpdatedAt = Now,
    };
}
