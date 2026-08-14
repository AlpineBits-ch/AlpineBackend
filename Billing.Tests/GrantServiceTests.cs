using Billing.Application.Services;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>Grant issuing, amending and revoking against a real Postgres.</summary>
[TestFixture]
public class GrantServiceTests
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
        // No plan rows, so the catalogue service answers entirely from the configured plans.
        _grants = new GrantService(
            _db, new PlanCatalogueService(_db, Plans.Catalogue()), _versions, _clock);
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    private static IssueGrant ProGrant(DateTimeOffset? expiresAt = null, string reason = "Beta tester.") =>
        new(SubjectKind.Guild, Subjects.Guild.Id, GrantKind.Plan, Plans.Pro, null, expiresAt, reason,
            GrantSource.Staff);

    [Test]
    public async Task Issuing_a_grant_stores_it_and_announces_the_subject()
    {
        var (grant, announcement) = await _grants.IssueAsync(
            ProGrant(Now.AddDays(90)), "user_staff", CancellationToken.None);

        await _db.SaveChangesAsync();

        var stored = await _db.Grants.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored.Id, Is.EqualTo(grant.Id));
            Assert.That(stored.Plan, Is.EqualTo(Plans.Pro));
            Assert.That(stored.CreatedBy, Is.EqualTo("user_staff"));
            Assert.That(stored.ExpiresAt, Is.EqualTo(Now.AddDays(90)));
            Assert.That(stored.RevokedAt, Is.Null);

            Assert.That(announcement.SubjectKind, Is.EqualTo(SubjectKind.Guild));
            Assert.That(announcement.SubjectId, Is.EqualTo(Subjects.Guild.Id));
            Assert.That(announcement.Reason, Is.EqualTo(EntitlementsChangedReason.GrantIssued));
            Assert.That(announcement.GrantId, Is.EqualTo(grant.Id));
            Assert.That(announcement.Version, Is.EqualTo(1));
            Assert.That(announcement.ChangedKeys, Does.Contain("voice.max_participants"));
        });
    }

    /// <summary>Required by monetization.md section 6, and refused before the database gets a chance
    /// to. The NOT NULL column is the backstop; this is the message an operator reads.</summary>
    [Test]
    public void A_grant_without_a_reason_is_refused()
    {
        foreach (var reason in new[] { "", "   ", "\t" })
        {
            var refusal = Assert.ThrowsAsync<GrantRefusedException>(
                () => _grants.IssueAsync(ProGrant(reason: reason), "user_staff", CancellationToken.None));

            Assert.That(refusal!.Code, Is.EqualTo(GrantErrorCodes.ReasonRequired));
        }
    }

    [Test]
    public void A_grant_naming_a_plan_the_instance_does_not_have_is_refused()
    {
        var request = new IssueGrant(SubjectKind.Guild, Subjects.Guild.Id, GrantKind.Plan,
            "enterprise", null, null, "Hand-onboarded customer.", GrantSource.Staff);

        var refusal = Assert.ThrowsAsync<GrantRefusedException>(
            () => _grants.IssueAsync(request, "user_staff", CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(GrantErrorCodes.UnknownPlan));
    }

    [Test]
    public void A_grant_naming_an_entitlement_key_that_does_not_exist_is_refused()
    {
        var request = new IssueGrant(SubjectKind.Guild, Subjects.Guild.Id, GrantKind.Entitlements,
            null, new Dictionary<string, string> { ["guild.teleportation"] = "true" }, null,
            "Because they asked nicely.", GrantSource.Staff);

        var refusal = Assert.ThrowsAsync<GrantRefusedException>(
            () => _grants.IssueAsync(request, "user_staff", CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(GrantErrorCodes.UnknownEntitlementKey));
    }

    /// <summary>A grant that has already lapsed grants nothing and looks, on the provenance screen,
    /// exactly like one that was applied and then expired.</summary>
    [Test]
    public void A_grant_that_has_already_expired_is_refused()
    {
        var refusal = Assert.ThrowsAsync<GrantRefusedException>(
            () => _grants.IssueAsync(ProGrant(Now.AddDays(-1)), "user_staff", CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(GrantErrorCodes.ExpiryInThePast));
    }

    /// <summary>Null expiry is permanent.</summary>
    [Test]
    public async Task A_permanent_grant_is_still_active_decades_later()
    {
        await _grants.IssueAsync(ProGrant(expiresAt: null), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromDays(365 * 25));

        var live = await _grants.ActiveGrantsAsync(Subjects.Guild, CancellationToken.None);
        var lapsed = await GrantExpirySweeper.CollectAsync(
            _db, _versions, _grants, _clock.GetUtcNow(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(live, Has.Count.EqualTo(1));
            Assert.That(lapsed, Is.Empty, "A permanent grant must never be swept as expired.");
        });
    }

    [Test]
    public async Task An_expired_grant_stops_being_active_and_stays_in_the_table()
    {
        await _grants.IssueAsync(ProGrant(Now.AddDays(30)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromDays(31));

        var live = await _grants.ActiveGrantsAsync(Subjects.Guild, CancellationToken.None);
        var all = await _grants.ListAsync(Subjects.Guild, activeOnly: false, CancellationToken.None);
        var rows = await _db.Grants.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(live, Is.Empty);
            Assert.That(all, Has.Count.EqualTo(1));
            Assert.That(rows, Is.EqualTo(1));
        });
    }

    /// <summary>The rule that is not negotiable: revocation is a field, never a delete.</summary>
    [Test]
    public async Task A_revoked_grant_is_still_a_row_with_both_reasons_intact()
    {
        var (issued, _) = await _grants.IssueAsync(
            ProGrant(reason: "Compensation for the outage on the 3rd."), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        var (revoked, announcement) = await _grants.RevokeAsync(
            issued.Id, "user_admin", "Customer moved to a paid plan.", CancellationToken.None);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var stored = await _db.Grants.SingleAsync();
        var live = await _grants.ActiveGrantsAsync(Subjects.Guild, CancellationToken.None);
        var rows = await _db.Grants.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.EqualTo(1), "The row must never be deleted.");
            Assert.That(stored.Reason, Is.EqualTo("Compensation for the outage on the 3rd."));
            Assert.That(stored.RevokeReason, Is.EqualTo("Customer moved to a paid plan."));
            Assert.That(stored.RevokedBy, Is.EqualTo("user_admin"));
            Assert.That(stored.RevokedAt, Is.EqualTo(Now));
            Assert.That(stored.CreatedBy, Is.EqualTo("user_staff"));

            Assert.That(live, Is.Empty, "A revoked grant contributes nothing.");
            Assert.That(revoked.IsActiveAt(Now), Is.False);
            Assert.That(announcement.Reason, Is.EqualTo(EntitlementsChangedReason.GrantRevoked));
            Assert.That(announcement.Version, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task A_revocation_without_a_reason_is_refused_and_leaves_the_grant_alone()
    {
        var (issued, _) = await _grants.IssueAsync(ProGrant(), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        var refusal = Assert.ThrowsAsync<GrantRefusedException>(
            () => _grants.RevokeAsync(issued.Id, "user_admin", "  ", CancellationToken.None));

        var stored = await _db.Grants.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(GrantErrorCodes.ReasonRequired));
            Assert.That(stored.RevokedAt, Is.Null);
        });
    }

    /// <summary>Revoking twice would overwrite who did it and why, which is the same erasure a delete
    /// would be, one field at a time.</summary>
    [Test]
    public async Task Revoking_a_revoked_grant_is_refused_rather_than_overwriting_the_first_revocation()
    {
        var (issued, _) = await _grants.IssueAsync(ProGrant(), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        await _grants.RevokeAsync(issued.Id, "user_admin", "First reason.", CancellationToken.None);
        await _db.SaveChangesAsync();

        var refusal = Assert.ThrowsAsync<GrantRefusedException>(
            () => _grants.RevokeAsync(issued.Id, "user_other", "Second reason.", CancellationToken.None));

        _db.ChangeTracker.Clear();
        var stored = await _db.Grants.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(GrantErrorCodes.AlreadyRevoked));
            Assert.That(stored.RevokeReason, Is.EqualTo("First reason."));
            Assert.That(stored.RevokedBy, Is.EqualTo("user_admin"));
        });
    }

    /// <summary>Converting a grant to a permanent one, which section 6 lists as an operation the
    /// console needs.</summary>
    [Test]
    public async Task Amending_the_expiry_to_null_makes_a_grant_permanent()
    {
        var (issued, _) = await _grants.IssueAsync(
            ProGrant(Now.AddDays(30)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        var (amended, announcement) = await _grants.AmendExpiryAsync(
            issued.Id, null, CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromDays(500));
        var live = await _grants.ActiveGrantsAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(amended.ExpiresAt, Is.Null);
            Assert.That(live, Has.Count.EqualTo(1));
            Assert.That(announcement.Reason, Is.EqualTo(EntitlementsChangedReason.GrantAmended));
        });
    }

    [Test]
    public async Task Amending_a_revoked_grant_is_refused()
    {
        var (issued, _) = await _grants.IssueAsync(ProGrant(), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        await _grants.RevokeAsync(issued.Id, "user_admin", "Done with it.", CancellationToken.None);
        await _db.SaveChangesAsync();

        var refusal = Assert.ThrowsAsync<GrantRefusedException>(
            () => _grants.AmendExpiryAsync(issued.Id, Now.AddDays(60), CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(GrantErrorCodes.AlreadyRevoked));
    }

    /// <summary>Listing is the audit trail, so it shows everything by default.</summary>
    [Test]
    public async Task Listing_returns_revoked_and_expired_grants_unless_asked_for_live_ones()
    {
        var (revoked, _) = await _grants.IssueAsync(ProGrant(), "user_staff", CancellationToken.None);
        await _grants.IssueAsync(ProGrant(Now.AddDays(10)), "user_staff", CancellationToken.None);
        await _grants.IssueAsync(ProGrant(), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        await _grants.RevokeAsync(revoked.Id, "user_admin", "Issued in error.", CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromDays(11));

        var all = await _grants.ListAsync(Subjects.Guild, activeOnly: false, CancellationToken.None);
        var live = await _grants.ListAsync(Subjects.Guild, activeOnly: true, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(all, Has.Count.EqualTo(3));
            Assert.That(live, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Grants_for_one_subject_are_not_returned_for_another()
    {
        await _grants.IssueAsync(ProGrant(), "user_staff", CancellationToken.None);
        await _grants.IssueAsync(
            new IssueGrant(SubjectKind.User, Subjects.User.Id, GrantKind.Entitlements, null,
                new Dictionary<string, string> { ["user.max_devices"] = "20" }, null,
                "Long-standing contributor.", GrantSource.Staff),
            "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        var guild = await _grants.ActiveGrantsAsync(Subjects.Guild, CancellationToken.None);
        var user = await _grants.ActiveGrantsAsync(Subjects.User, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(guild, Has.Count.EqualTo(1));
            Assert.That(guild[0].Plan, Is.EqualTo(Plans.Pro));
            Assert.That(user, Has.Count.EqualTo(1));
            Assert.That(user[0].Entitlements!["user.max_devices"], Is.EqualTo("20"));
        });
    }

    /// <summary>An expiry is a date passing rather than a write, so something has to notice on
    /// everyone's behalf or the caches hold the expired value until their TTL.</summary>
    [Test]
    public async Task The_expiry_sweep_announces_a_lapsed_grant_once_per_subject()
    {
        await _grants.IssueAsync(ProGrant(Now.AddMinutes(1)), "user_staff", CancellationToken.None);
        await _grants.IssueAsync(ProGrant(Now.AddMinutes(2)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        var before = await GrantExpirySweeper.CollectAsync(
            _db, _versions, _grants, Now, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(3));
        var after = await GrantExpirySweeper.CollectAsync(
            _db, _versions, _grants, _clock.GetUtcNow(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.Empty, "Nothing has lapsed yet.");
            Assert.That(after, Has.Count.EqualTo(1),
                "Two grants on one subject are one change, not two.");
            Assert.That(after[0].Reason, Is.EqualTo(EntitlementsChangedReason.GrantExpired));
            Assert.That(after[0].SubjectId, Is.EqualTo(Subjects.Guild.Id));
            Assert.That(after[0].Version, Is.EqualTo(3), "Two issues, then one expiry announcement.");
        });
    }

    /// <summary>Revocation already announced itself.</summary>
    [Test]
    public async Task The_expiry_sweep_ignores_a_grant_that_was_revoked_before_its_date()
    {
        var (issued, _) = await _grants.IssueAsync(
            ProGrant(Now.AddMinutes(1)), "user_staff", CancellationToken.None);
        await _db.SaveChangesAsync();

        await _grants.RevokeAsync(issued.Id, "user_admin", "Ended early.", CancellationToken.None);
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromMinutes(3));
        var lapsed = await GrantExpirySweeper.CollectAsync(
            _db, _versions, _grants, _clock.GetUtcNow(), CancellationToken.None);

        Assert.That(lapsed, Is.Empty);
    }
}
