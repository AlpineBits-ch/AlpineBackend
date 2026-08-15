using Billing.Application.Promotions;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>
/// The permanent record itself: what a redemption writes, what a move does to it, and what expiry
/// does and does not do.
/// </summary>
[TestFixture]
public class PromotionRedemptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private PromotionCampaignService _campaigns = null!;
    private PromotionRedemptionService _redemptions = null!;
    private PromotionCampaign _campaign = null!;

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

        _campaign = await _campaigns.CreateAsync(
            PromotionFixtures.Open(trialDays: 30), PromotionFixtures.Staff, CancellationToken.None);

        await _db.SaveChangesAsync();
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    private async Task<PromotionRedemptionResult> RecordAsync(
        string owner = PromotionFixtures.Owner,
        string? guild = PromotionFixtures.Guild,
        PromotionIdentityHashes? hashes = null)
    {
        var result = await _redemptions.RecordAsync(
            _campaign, owner, guild, hashes ?? PromotionIdentityHashes.None, null, CancellationToken.None);

        await _db.SaveChangesAsync();
        return result;
    }

    // ── Recording ────────────────────────────────────────────────────────────

    /// <summary>Both rows, in one call, because "the owner was recorded and the guild was not" is a
    /// free second trial for the next owner of that guild.</summary>
    [Test]
    public async Task A_redemption_writes_a_row_for_the_owner_and_one_for_the_guild()
    {
        await RecordAsync();
        _db.ChangeTracker.Clear();

        var rows = await _db.PromotionRedemptions.OrderBy(row => row.SubjectKind).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Select(row => row.SubjectKind),
                Is.EquivalentTo(new[] { SubjectKind.User, SubjectKind.Guild }));
            Assert.That(rows.All(row => row.OwnerUserId == PromotionFixtures.Owner), Is.True,
                "the guild row names the owner too, so a refusal can say who took it");
            Assert.That(rows.All(row => row.EndsAt == Now.AddDays(30)), Is.True);
        });
    }

    [Test]
    public async Task A_user_scoped_redemption_writes_only_the_owner_row()
    {
        await RecordAsync(guild: null);
        _db.ChangeTracker.Clear();

        var rows = await _db.PromotionRedemptions.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].SubjectKind, Is.EqualTo(SubjectKind.User));
        });
    }

    /// <summary>The marks hang off the owner row rather than the guild one, because the identity
    /// belongs to the person and a mark on a guild row would be orphaned the moment the trial
    /// moved.</summary>
    [Test]
    public async Task Identity_marks_are_written_against_the_owner_row()
    {
        var hashes = new PromotionIdentityHashes("phone-hash", ["device-hash-a", "device-hash-b"], "card-hash");

        var result = await RecordAsync(hashes: hashes);
        _db.ChangeTracker.Clear();

        var marks = await _db.PromotionIdentityMarks.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(marks, Has.Count.EqualTo(4));
            Assert.That(marks.All(mark => mark.RedemptionId == result.Owner.Id), Is.True);
            Assert.That(marks.Count(mark => mark.Kind == PromotionIdentityKind.Device), Is.EqualTo(2));
            Assert.That(marks.Count(mark => mark.Kind == PromotionIdentityKind.Phone), Is.EqualTo(1));
            Assert.That(marks.Count(mark => mark.Kind == PromotionIdentityKind.Card), Is.EqualTo(1));
        });
    }

    /// <summary>An account with no number and no card produces no marks for them, rather than marks
    /// over the empty string - which would match every other account that also has neither.</summary>
    [Test]
    public async Task An_absent_signal_writes_no_mark()
    {
        await RecordAsync(hashes: new PromotionIdentityHashes(null, ["device-hash"], null));
        _db.ChangeTracker.Clear();

        var marks = await _db.PromotionIdentityMarks.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(marks, Has.Count.EqualTo(1));
            Assert.That(marks[0].Kind, Is.EqualTo(PromotionIdentityKind.Device));
        });
    }

    /// <summary>The index behind the identity control, exercised directly: two accounts cannot both
    /// carry a mark for one device under one campaign, whatever the service check did.</summary>
    [Test]
    public async Task The_database_refuses_two_marks_for_one_identity()
    {
        var first = await RecordAsync(hashes: new PromotionIdentityHashes(null, ["device-hash"], null));

        PromotionFixtures.SeedMark(
            _db, _campaign, first.Owner, PromotionIdentityKind.Device, "device-hash", Now);

        Assert.That(async () => await _db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    // ── Moving ───────────────────────────────────────────────────────────────

    /// <summary><b>The clock is copied, never recomputed.</b> Section 7.2 allows the move precisely
    /// because it gains nothing, and this is the assertion that keeps it that way.</summary>
    [Test]
    public async Task A_trial_moved_between_guilds_keeps_its_original_clock()
    {
        await RecordAsync();

        _clock.Advance(TimeSpan.FromDays(10));
        _db.ChangeTracker.Clear();

        var campaign = await _db.PromotionCampaigns.SingleAsync();

        var moved = await _redemptions.MoveGuildAsync(
            campaign, PromotionFixtures.Owner, PromotionFixtures.OtherGuild, CancellationToken.None);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var rows = await _db.PromotionRedemptions
            .Where(row => row.SubjectKind == SubjectKind.Guild)
            .OrderBy(row => row.SubjectId)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(moved.EndsAt, Is.EqualTo(Now.AddDays(30)),
                "ten days in, and it still ends when it was always going to");
            Assert.That(moved.RedeemedAt, Is.EqualTo(Now));
            Assert.That(rows, Has.Count.EqualTo(2), "the guild it left keeps its row");
        });
    }

    /// <summary>The row of the guild the trial left stays and still refuses.</summary>
    [Test]
    public async Task The_guild_a_trial_moved_away_from_is_released_and_still_blocks()
    {
        await RecordAsync();
        _db.ChangeTracker.Clear();

        var campaign = await _db.PromotionCampaigns.SingleAsync();

        await _redemptions.MoveGuildAsync(
            campaign, PromotionFixtures.Owner, PromotionFixtures.OtherGuild, CancellationToken.None);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var left = await _db.PromotionRedemptions.SingleAsync(
            row => row.SubjectId == PromotionFixtures.Guild);

        var decision = await PromotionFixtures
            .Eligibility(_db, _clock, PromotionFixtures.IdentitySaying())
            .EvaluateAsync(
                await _db.PromotionCampaigns.SingleAsync(),
                PromotionFixtures.OtherOwner,
                PromotionFixtures.Guild,
                card: null,
                CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(left.ReleasedAt, Is.Not.Null);
            Assert.That(decision.Code, Is.EqualTo(PromotionErrorCodes.GuildAlreadyRedeemed));
        });
    }

    [Test]
    public async Task Moving_a_trial_to_a_guild_that_has_had_one_is_refused()
    {
        await RecordAsync();
        await RecordAsync(owner: PromotionFixtures.OtherOwner, guild: PromotionFixtures.OtherGuild);
        _db.ChangeTracker.Clear();

        var campaign = await _db.PromotionCampaigns.SingleAsync();

        Assert.That(
            async () => await _redemptions.MoveGuildAsync(
                campaign, PromotionFixtures.Owner, PromotionFixtures.OtherGuild, CancellationToken.None),
            Throws.InstanceOf<PromotionRefusedException>()
                .With.Property(nameof(PromotionRefusedException.Code))
                .EqualTo(PromotionErrorCodes.GuildAlreadyRedeemed));
    }

    /// <summary>Changing their mind is allowed and gains nothing: the released row is re-attached
    /// rather than a second one written, so the clock is the same clock and the guild is still barred
    /// from anybody else's trial.</summary>
    [Test]
    public async Task Moving_a_trial_back_to_a_guild_it_left_reuses_the_row_and_the_clock()
    {
        await RecordAsync();
        _db.ChangeTracker.Clear();

        var campaign = await _db.PromotionCampaigns.SingleAsync();

        await _redemptions.MoveGuildAsync(
            campaign, PromotionFixtures.Owner, PromotionFixtures.OtherGuild, CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromDays(5));
        _db.ChangeTracker.Clear();

        var back = await _redemptions.MoveGuildAsync(
            await _db.PromotionCampaigns.SingleAsync(), PromotionFixtures.Owner,
            PromotionFixtures.Guild, CancellationToken.None);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var guildRows = await _db.PromotionRedemptions
            .Where(row => row.SubjectKind == SubjectKind.Guild)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(back.ReleasedAt, Is.Null);
            Assert.That(back.EndsAt, Is.EqualTo(Now.AddDays(30)), "still the original clock");
            Assert.That(guildRows, Has.Count.EqualTo(2), "no third row was written");
            Assert.That(
                guildRows.Single(row => row.SubjectId == PromotionFixtures.OtherGuild).ReleasedAt,
                Is.Not.Null);
        });
    }

    [Test]
    public async Task Moving_a_trial_that_has_ended_is_refused()
    {
        await RecordAsync();

        _clock.Advance(TimeSpan.FromDays(31));
        _db.ChangeTracker.Clear();

        var campaign = await _db.PromotionCampaigns.SingleAsync();

        Assert.That(
            async () => await _redemptions.MoveGuildAsync(
                campaign, PromotionFixtures.Owner, PromotionFixtures.OtherGuild, CancellationToken.None),
            Throws.InstanceOf<PromotionRefusedException>()
                .With.Property(nameof(PromotionRefusedException.Code))
                .EqualTo(PromotionErrorCodes.NoTrialToMove));
    }

    [Test]
    public void Moving_a_trial_that_was_never_taken_is_refused()
    {
        Assert.That(
            async () => await _redemptions.MoveGuildAsync(
                _campaign, PromotionFixtures.Owner, PromotionFixtures.OtherGuild, CancellationToken.None),
            Throws.InstanceOf<PromotionRefusedException>()
                .With.Property(nameof(PromotionRefusedException.Code))
                .EqualTo(PromotionErrorCodes.NoTrialToMove));
    }

    /// <summary>Moving to the guild it is already on is a no-op rather than a second row, which the
    /// unique index would refuse anyway. A double-clicked button is not an error.</summary>
    [Test]
    public async Task Moving_a_trial_to_the_guild_it_is_already_on_changes_nothing()
    {
        var original = await RecordAsync();
        _db.ChangeTracker.Clear();

        var campaign = await _db.PromotionCampaigns.SingleAsync();

        var moved = await _redemptions.MoveGuildAsync(
            campaign, PromotionFixtures.Owner, PromotionFixtures.Guild, CancellationToken.None);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Multiple(async () =>
        {
            Assert.That(moved.Id, Is.EqualTo(original.Guild!.Id));
            Assert.That(await _db.PromotionRedemptions.CountAsync(), Is.EqualTo(2));
            Assert.That(moved.ReleasedAt, Is.Null);
        });
    }

    // ── Expiry ───────────────────────────────────────────────────────────────

    /// <summary>Expiry stamps and does not delete.</summary>
    [Test]
    public async Task Expiry_stamps_a_field_and_deletes_nothing()
    {
        await RecordAsync();

        _clock.Advance(TimeSpan.FromDays(31));

        var stamped = await _redemptions.StampExpiredAsync(100, CancellationToken.None);
        _db.ChangeTracker.Clear();

        var rows = await _db.PromotionRedemptions.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stamped, Is.EqualTo(2));
            Assert.That(rows, Has.Count.EqualTo(2), "nothing was removed");
            Assert.That(rows.All(row => row.ExpiredAt == Now.AddDays(31)), Is.True);
        });
    }

    [Test]
    public async Task A_redemption_that_has_not_ended_is_not_stamped()
    {
        await RecordAsync();

        _clock.Advance(TimeSpan.FromDays(29));

        var stamped = await _redemptions.StampExpiredAsync(100, CancellationToken.None);
        _db.ChangeTracker.Clear();

        Assert.Multiple(async () =>
        {
            Assert.That(stamped, Is.Zero);
            Assert.That(await _db.PromotionRedemptions.CountAsync(row => row.ExpiredAt != null), Is.Zero);
        });
    }

    /// <summary>The sweep claims each row once, so a second pass over the same lapsed redemptions
    /// writes nothing and cannot move a stamp that already says when it ran out.</summary>
    [Test]
    public async Task A_second_expiry_pass_stamps_nothing_again()
    {
        await RecordAsync();

        _clock.Advance(TimeSpan.FromDays(31));
        await _redemptions.StampExpiredAsync(100, CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(1));
        var second = await _redemptions.StampExpiredAsync(100, CancellationToken.None);

        _db.ChangeTracker.Clear();

        Assert.Multiple(async () =>
        {
            Assert.That(second, Is.Zero);
            Assert.That(
                (await _db.PromotionRedemptions.FirstAsync()).ExpiredAt, Is.EqualTo(Now.AddDays(31)));
        });
    }
}
