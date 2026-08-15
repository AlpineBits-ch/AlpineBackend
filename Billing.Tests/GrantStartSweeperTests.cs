using Billing.Application.Services;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>
/// The opposite boundary to <see cref="GrantExpirySweeper"/>: a queued grant's start date arriving.
/// </summary>
[TestFixture]
public class GrantStartSweeperTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private GrantService _grants = null!;
    private EntitlementVersionService _versions = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        _clock = new TestClock(Now);
        _versions = new EntitlementVersionService(_db);
        _grants = new GrantService(
            _db, new PlanCatalogueService(_db, Plans.Catalogue()), _versions, _clock);
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    /// <summary>What a credit purchase queued behind an existing grant looks like: a real, unrevoked
    /// row that contributes nothing until its day.</summary>
    private static IssueGrant QueuedProGrant(DateTimeOffset? startsAt, DateTimeOffset? expiresAt) =>
        new(SubjectKind.Guild, Subjects.Guild.Id, GrantKind.Plan, Plans.Pro, null, expiresAt,
            "Thirty days bought with credit.", GrantSource.Credit, startsAt);

    private Task<IReadOnlyList<EntitlementsChanged>> SweepStarts() =>
        GrantStartSweeper.CollectAsync(_db, _versions, _grants, _clock.GetUtcNow(), CancellationToken.None);

    private Task<IReadOnlyList<EntitlementsChanged>> SweepExpiries() =>
        GrantExpirySweeper.CollectAsync(_db, _versions, _grants, _clock.GetUtcNow(), CancellationToken.None);

    /// <summary>The case the field exists for.</summary>
    [Test]
    public async Task The_start_sweep_announces_a_grant_whose_start_date_just_passed()
    {
        await _grants.IssueAsync(
            QueuedProGrant(Now.AddMinutes(1), Now.AddDays(30)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        var before = await SweepStarts();

        _clock.Advance(TimeSpan.FromMinutes(3));
        var after = await SweepStarts();

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.Empty, "It has not started yet.");
            Assert.That(after, Has.Count.EqualTo(1));
            Assert.That(after[0].Reason, Is.EqualTo(EntitlementsChangedReason.GrantStarted));
            Assert.That(after[0].SubjectId, Is.EqualTo(Subjects.Guild.Id));
            Assert.That(after[0].Version, Is.EqualTo(2), "One issue, then one start announcement.");
            Assert.That(after[0].ChangedKeys, Does.Contain("voice.max_participants"));
        });
    }

    /// <summary>Two grants on one subject are one change.</summary>
    [Test]
    public async Task Two_grants_starting_in_the_same_window_produce_one_event()
    {
        await _grants.IssueAsync(
            QueuedProGrant(Now.AddMinutes(1), Now.AddDays(30)), "user_staff", CancellationToken.None);
        await _grants.IssueAsync(
            QueuedProGrant(Now.AddMinutes(2), Now.AddDays(60)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromMinutes(3));
        var announced = await SweepStarts();

        Assert.Multiple(() =>
        {
            Assert.That(announced, Has.Count.EqualTo(1),
                "Two grants on one subject are one change, not two.");
            Assert.That(announced[0].Version, Is.EqualTo(3), "Two issues, then one start announcement.");
        });
    }

    /// <summary>The lookback is what makes repeats free, and it is also what stops them being
    /// forever. A grant that started an hour ago was announced by an earlier pass.</summary>
    [Test]
    public async Task A_grant_that_started_before_the_lookback_window_is_not_announced()
    {
        await _grants.IssueAsync(
            QueuedProGrant(Now.AddMinutes(1), Now.AddDays(30)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.That(await SweepStarts(), Is.Empty);
    }

    /// <summary>Revocation already announced itself, and a grant revoked before its start date never
    /// counted for an instant. Announcing it as started would put a second and wrong reason in the log
    /// an operator reads when asking why something changed.</summary>
    [Test]
    public async Task A_revoked_grant_whose_start_date_passes_is_not_announced()
    {
        var (issued, _) = await _grants.IssueAsync(
            QueuedProGrant(Now.AddMinutes(1), Now.AddDays(30)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        await _grants.RevokeAsync(issued.Id, "user_admin", "Purchase reversed.", CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromMinutes(3));

        Assert.That(await SweepStarts(), Is.Empty);
    }

    /// <summary>
    /// A null start means "immediately", which is what every grant issued before wave 8 meant and
    /// still means.
    /// </summary>
    [Test]
    public async Task A_grant_with_no_start_date_is_never_announced_by_this_sweep()
    {
        await _grants.IssueAsync(
            QueuedProGrant(null, Now.AddDays(30)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        var immediately = await SweepStarts();

        _clock.Advance(TimeSpan.FromMinutes(3));
        var later = await SweepStarts();

        _clock.Advance(TimeSpan.FromDays(400));
        var muchLater = await SweepStarts();

        Assert.Multiple(() =>
        {
            Assert.That(immediately, Is.Empty);
            Assert.That(later, Is.Empty);
            Assert.That(muchLater, Is.Empty);
        });
    }

    /// <summary>
    /// The first of the two edges: a grant that started and finished inside one lookback window.
    /// </summary>
    [Test]
    public async Task A_grant_that_started_and_expired_in_the_same_window_is_announced_only_as_expired()
    {
        await _grants.IssueAsync(
            QueuedProGrant(Now.AddMinutes(1), Now.AddMinutes(2)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromMinutes(3));

        var starts = await SweepStarts();
        var expiries = await SweepExpiries();

        Assert.Multiple(() =>
        {
            Assert.That(starts, Is.Empty, "It is over; saying it started would contradict resolution.");
            Assert.That(expiries, Has.Count.EqualTo(1), "The invalidation still has to happen.");
            Assert.That(expiries[0].Reason, Is.EqualTo(EntitlementsChangedReason.GrantExpired));
        });
    }

    /// <summary>
    /// The second edge: already expired at <c>now</c> excludes a grant even when its expiry did not
    /// fall in the same window as its start.
    /// </summary>
    [Test]
    public async Task A_queued_grant_amended_to_expire_before_it_begins_is_not_announced_as_started()
    {
        var (issued, _) = await _grants.IssueAsync(
            QueuedProGrant(Now.AddMinutes(10), Now.AddDays(30)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        await _grants.AmendExpiryAsync(issued.Id, Now.AddMinutes(5), CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.That(await SweepStarts(), Is.Empty);
    }

    /// <summary>The queued purchase from monetization.md section 8.3, end to end: bought while the
    /// guild is already Pro, silent until the grant in front of it runs out, then announced.</summary>
    [Test]
    public async Task A_purchase_queued_behind_a_live_grant_is_announced_when_its_turn_comes()
    {
        await _grants.IssueAsync(
            new IssueGrant(SubjectKind.Guild, Subjects.Guild.Id, GrantKind.Plan, Plans.Pro, null,
                Now.AddMinutes(2), "The month they already had.", GrantSource.Staff),
            "user_staff", CancellationToken.None);

        await _grants.IssueAsync(
            QueuedProGrant(Now.AddMinutes(2), Now.AddDays(30)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        var beforeHandover = await SweepStarts();
        var queuedIsInvisible = await _grants.ActiveGrantsAsync(Subjects.Guild, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(3));

        var afterHandover = await SweepStarts();
        var queuedIsLive = await _grants.ActiveGrantsAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(beforeHandover, Is.Empty);
            Assert.That(queuedIsInvisible, Has.Count.EqualTo(1),
                "The queued month must not overlap the one in front of it.");
            Assert.That(afterHandover, Has.Count.EqualTo(1));
            Assert.That(afterHandover[0].Reason, Is.EqualTo(EntitlementsChangedReason.GrantStarted));
            Assert.That(queuedIsLive, Has.Count.EqualTo(1), "The queued month is now the live one.");
        });
    }
}
