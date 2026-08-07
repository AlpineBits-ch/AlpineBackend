using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>Pure domain behaviour for the maintenance module: the service cadence, which is
/// deliberately the opposite of the chore cadence, and the two warranty cutoffs.</summary>
[TestFixture]
public class MaintenanceDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static MaintenanceAsset MakeAsset(
        int? intervalDays = 365,
        DateTimeOffset? lastServicedAt = null,
        DateTimeOffset? purchasedAt = null,
        DateTimeOffset? warrantyUntil = null) =>
        MaintenanceAsset.Create(new CreateMaintenanceAssetParams
        {
            ChannelId = "chan-upkeep",
            GuildId = "guild-1",
            Name = "Boiler",
            ServiceIntervalDays = intervalDays,
            LastServicedAt = lastServicedAt,
            PurchasedAt = purchasedAt,
            WarrantyUntil = warrantyUntil,
            AddedByUserId = "anna",
        });

    // ══════════════════════════════════════════════════════════════════════════ The service
    // cadence ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void RecordService_SchedulesTheNextOneFromTheVisit()
    {
        var asset = MakeAsset(intervalDays: 365, lastServicedAt: Now.AddYears(-1));

        asset.RecordService(Now);

        Assert.Multiple(() =>
        {
            Assert.That(asset.LastServicedAt, Is.EqualTo(Now));
            Assert.That(asset.NextServiceAt, Is.EqualTo(Now.AddDays(365)));
        });
    }

    /// <summary>
    /// The behaviour that separates this from <see cref="Chore.AdvanceFrom"/>, and the one somebody
    /// will eventually try to "fix".
    /// </summary>
    [Test]
    public void RecordService_ServicedTwoMonthsLate_CountsFromTheVisitNotTheDueDate()
    {
        var wasDueAt = Now.AddMonths(-2);
        var asset = MakeAsset(intervalDays: 365, lastServicedAt: wasDueAt.AddDays(-365));

        Assert.That(asset.NextServiceAt, Is.EqualTo(wasDueAt), "sanity: it was due two months ago");

        asset.RecordService(Now);

        Assert.Multiple(() =>
        {
            Assert.That(asset.NextServiceAt, Is.EqualTo(Now.AddDays(365)),
                "twelve months after the engineer came, not after the date it was missed");
            Assert.That(asset.NextServiceAt, Is.Not.EqualTo(wasDueAt.AddDays(365)),
                "which is what a Chore-style anchored cadence would have produced");
        });
    }

    [Test]
    public void RecordService_WithNoInterval_LeavesNothingScheduled()
    {
        var asset = MakeAsset(intervalDays: null);

        asset.RecordService(Now);

        Assert.Multiple(() =>
        {
            Assert.That(asset.LastServicedAt, Is.EqualTo(Now), "the visit still happened");
            Assert.That(asset.NextServiceAt, Is.Null,
                "a sofa catalogued for its warranty is never 'due' for anything");
        });
    }

    [Test]
    public void RecordService_ReleasesTheDueStamp()
    {
        var asset = MakeAsset(intervalDays: 90, lastServicedAt: Now.AddDays(-100));
        asset.ServiceNotifiedAt = Now.AddDays(-1);

        asset.RecordService(Now);

        Assert.That(asset.ServiceNotifiedAt, Is.Null,
            "the fact the warning described has stopped being true, so the next due date announces itself");
    }

    [Test]
    public void Create_SchedulesFromTheLastServiceWhenThereIsOne()
    {
        var asset = MakeAsset(intervalDays: 365, lastServicedAt: Now.AddDays(-240));

        Assert.That(asset.NextServiceAt, Is.EqualTo(Now.AddDays(-240).AddDays(365)),
            "entering a boiler serviced eight months ago schedules the next one four months out");
    }

    [Test]
    public void Create_FallsBackToThePurchaseDate()
    {
        var asset = MakeAsset(intervalDays: 365, purchasedAt: Now.AddDays(-30));

        Assert.That(asset.NextServiceAt, Is.EqualTo(Now.AddDays(-30).AddDays(365)));
    }

    [Test]
    public void Create_WithNoIntervalAtAll_SchedulesNothing()
    {
        Assert.That(MakeAsset(intervalDays: null).NextServiceAt, Is.Null);
    }

    /// <summary>A zero or negative interval is somebody's typo, not a request to service the boiler
    /// every day forever.</summary>
    [Test]
    public void Create_WithANonsensicalInterval_SchedulesNothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MakeAsset(intervalDays: 0).NextServiceAt, Is.Null);
            Assert.That(MakeAsset(intervalDays: -7).NextServiceAt, Is.Null);
        });
    }

    [Test]
    public void IsServiceOverdue_IsFalseWithNothingScheduled()
    {
        Assert.That(MakeAsset(intervalDays: null).IsServiceOverdue(Now), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════ Warranty cutoffs,
    // on both sides ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void IsWarrantyExpiring_InsideTheWindow_IsTrue()
    {
        Assert.That(MakeAsset(warrantyUntil: Now.AddDays(29)).IsWarrantyExpiring(Now), Is.True);
    }

    [Test]
    public void IsWarrantyExpiring_OutsideTheWindow_IsFalse()
    {
        Assert.That(MakeAsset(warrantyUntil: Now.AddDays(31)).IsWarrantyExpiring(Now), Is.False,
            "a warranty with a month and a day left is not news yet");
    }

    /// <summary>The other edge, and the one that matters for the board: a lapsed warranty has
    /// nothing left to do about it, so a board that keeps listing it is a board people stop
    /// reading.</summary>
    [Test]
    public void IsWarrantyExpiring_AlreadyLapsed_IsFalse()
    {
        Assert.That(MakeAsset(warrantyUntil: Now.AddDays(-1)).IsWarrantyExpiring(Now), Is.False);
    }

    [Test]
    public void IsWarrantyExpiring_WithNoWarranty_IsFalse()
    {
        Assert.That(MakeAsset(warrantyUntil: null).IsWarrantyExpiring(Now), Is.False);
    }

    [Test]
    public void WarrantyWarnAt_IsThirtyDaysBeforeExpiry()
    {
        var asset = MakeAsset(warrantyUntil: Now.AddDays(100));

        Assert.Multiple(() =>
        {
            Assert.That(asset.WarrantyWarnAt, Is.EqualTo(Now.AddDays(70)));
            Assert.That(MakeAsset(warrantyUntil: null).WarrantyWarnAt, Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ What counts as
    // needing a human ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void NeedsAttention_CoversBrokenOverdueAndExpiring()
    {
        var broken = MakeAsset(intervalDays: null);
        broken.Status = AssetStatus.Broken;

        var overdue = MakeAsset(intervalDays: 30, lastServicedAt: Now.AddDays(-60));
        var expiring = MakeAsset(intervalDays: null, warrantyUntil: Now.AddDays(10));

        Assert.Multiple(() =>
        {
            Assert.That(broken.NeedsAttention(Now), Is.True);
            Assert.That(overdue.NeedsAttention(Now), Is.True);
            Assert.That(expiring.NeedsAttention(Now), Is.True);
        });
    }

    /// <summary>Out of service is a decision the house already made, not a job waiting.</summary>
    [Test]
    public void NeedsAttention_ExcludesSomethingTakenOutOfUseDeliberately()
    {
        var asset = MakeAsset(intervalDays: null);
        asset.Status = AssetStatus.OutOfService;

        Assert.That(asset.NeedsAttention(Now), Is.False);
    }

    [Test]
    public void NeedsAttention_AHealthyAssetIsNotOnTheBoard()
    {
        var asset = MakeAsset(intervalDays: 365, lastServicedAt: Now, warrantyUntil: Now.AddYears(2));

        Assert.That(asset.NeedsAttention(Now), Is.False);
    }
}
