using Billing.Application.Services;
using Billing.Contracts.Bus.Events;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>The editable plan catalogue, against a real Postgres.</summary>
[TestFixture]
public class PlanServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private const string Admin = "user_admin";

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private PlanCatalogueService _catalogue = null!;
    private EntitlementVersionService _versions = null!;
    private PlanService _plans = null!;
    private GrantService _grants = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        _clock = new TestClock(Now);
        _catalogue = new PlanCatalogueService(_db, Plans.Catalogue());
        _versions = new EntitlementVersionService(_db);
        _plans = new PlanService(_db, _catalogue, _versions, Plans.Options(), _clock);
        _grants = new GrantService(_db, _catalogue, _versions, _clock);
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    private static Dictionary<string, string> ProValues(string participants = "50") => new()
    {
        ["voice.max_participants"] = participants,
        ["voice.video_ceiling"] = "1080p60",
        ["guild.emoji_slots"] = "500",
        ["guild.vanity_url"] = "true",
    };

    private async Task<Plan> CreateProAsync(long? price = 2900)
    {
        var (plan, _) = await _plans.CreateAsync(
            new CreatePlan("pro", "Pro", null, ProValues(), price, "usd", "The launch tier."),
            Admin, CancellationToken.None);

        await _db.SaveChangesAsync();
        return plan;
    }

    // ── Versioning, which is the whole package ───────────────────────────────

    [Test]
    public async Task Creating_a_plan_writes_version_one_and_records_who_did_it()
    {
        var plan = await CreateProAsync();

        var version = await _db.PlanVersions.SingleAsync();
        var audit = await _db.PlanAuditEntries.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(plan.CurrentVersionNumber, Is.EqualTo(1));
            Assert.That(plan.SeededFromConfiguration, Is.False);
            Assert.That(version.VersionNumber, Is.EqualTo(1));
            Assert.That(version.PriceMinorUnits, Is.EqualTo(2900));
            Assert.That(version.Currency, Is.EqualTo("usd"));
            Assert.That(version.CreatedBy, Is.EqualTo(Admin));
            Assert.That(audit.Action, Is.EqualTo(PlanChangeAction.PlanCreated));
            Assert.That(audit.Reason, Is.EqualTo("The launch tier."));
            Assert.That(audit.Actor, Is.EqualTo(Admin));
        });
    }

    /// <summary>The one that matters.</summary>
    [Test]
    public async Task An_edit_writes_a_new_version_and_leaves_existing_subjects_on_the_old_one()
    {
        var plan = await CreateProAsync();

        await _plans.AssignAsync(Subjects.Guild, "pro", null, "Onboarded customer.", Admin,
            CancellationToken.None);
        await _db.SaveChangesAsync();

        await _plans.EditAsync("pro",
            new EditPlan(ProValues(participants: "20"), 2900, "usd", "Cutting the room size."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var stored = await _db.Plans.SingleAsync();
        var assignment = await _db.PlanAssignments.SingleAsync();
        var resolved = await _catalogue.PlanForAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(stored.CurrentVersionNumber, Is.EqualTo(2), "the plan now sells version 2");
            Assert.That(assignment.VersionNumber, Is.EqualTo(1),
                "an edit must never move an existing subject");
            Assert.That(resolved!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber,
                Is.EqualTo(50),
                "the guild keeps the ceiling it was on, which is the whole promise of versioning");
            Assert.That(resolved.Name, Is.EqualTo("pro@1"));
        });
    }

    [Test]
    public async Task A_subject_moved_to_the_new_version_gets_the_new_values()
    {
        await CreateProAsync();

        await _plans.AssignAsync(Subjects.Guild, "pro", null, "Onboarded customer.", Admin,
            CancellationToken.None);
        await _db.SaveChangesAsync();

        await _plans.EditAsync("pro",
            new EditPlan(ProValues(participants: "120"), 3900, "usd", "Bigger rooms, higher price."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var (assignment, announcement) = await _plans.AssignAsync(
            Subjects.Guild, "pro", 2, "Customer accepted the new terms.", Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var resolved = await _catalogue.PlanForAsync(Subjects.Guild, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(assignment.VersionNumber, Is.EqualTo(2));
            Assert.That(resolved!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber, Is.EqualTo(120));
            Assert.That(announcement.Reason, Is.EqualTo(EntitlementsChangedReason.PlanAssignmentChanged));
            Assert.That(announcement.SubjectId, Is.EqualTo(Subjects.Guild.Id));
        });
    }

    /// <summary>Rolling back is an activation with no new numbers in it, and it is the change an
    /// operator most needs to find in the log afterwards - which is why activation is recorded in
    /// the audit table rather than as a field on the version it would overwrite.</summary>
    [Test]
    public async Task Activating_an_earlier_version_makes_it_current_again_and_is_recorded_separately()
    {
        await CreateProAsync();

        await _plans.EditAsync("pro",
            new EditPlan(ProValues(participants: "20"), 2900, "usd", "A number somebody typed wrong."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        await _plans.ActivateAsync("pro", 1, "Rolling back the typo.", "user_other",
            CancellationToken.None);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var plan = await _db.Plans.SingleAsync();
        var activations = await _db.PlanAuditEntries
            .Where(entry => entry.Action == PlanChangeAction.VersionActivated)
            .ToListAsync();
        var versions = await _db.PlanVersions.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(plan.CurrentVersionNumber, Is.EqualTo(1));
            Assert.That(versions, Is.EqualTo(2), "a rollback re-uses a version, it does not write one");
            Assert.That(activations, Has.Count.EqualTo(1));
            Assert.That(activations[0].Actor, Is.EqualTo("user_other"));
            Assert.That(activations[0].Reason, Is.EqualTo("Rolling back the typo."));
        });
    }

    // ── Blast radius ─────────────────────────────────────────────────────────

    /// <summary>The number an admin is about to make a decision on, so double counting a subject
    /// that holds both a grant and an assignment is not a rounding error.</summary>
    [Test]
    public async Task The_blast_radius_counts_each_subject_once_and_splits_them_by_version()
    {
        await CreateProAsync();

        await _plans.AssignAsync(EntitlementSubject.ForGuild("gild_a"), "pro", null, "One.", Admin,
            CancellationToken.None);
        await _plans.AssignAsync(EntitlementSubject.ForGuild("gild_b"), "pro", null, "Two.", Admin,
            CancellationToken.None);
        await _db.SaveChangesAsync();

        await _plans.EditAsync("pro", new EditPlan(ProValues("60"), 2900, "usd", "More room."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        await _plans.AssignAsync(EntitlementSubject.ForGuild("gild_c"), "pro", null, "Three.", Admin,
            CancellationToken.None);

        // A grant naming the bare plan follows whatever is current, so it belongs to version 2.
        await _grants.IssueAsync(
            new IssueGrant(SubjectKind.Guild, "gild_d", GrantKind.Plan, "pro", null, null,
                "Hand-onboarded.", GrantSource.Staff), Admin, CancellationToken.None);
        await _grants.IssueAsync(
            new IssueGrant(SubjectKind.Guild, "gild_a", GrantKind.Plan, "pro", null, null,
                "Compensation on top of their plan.", GrantSource.Staff), Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var radius = await _plans.BlastRadiusAsync("pro", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(radius.TotalSubjects, Is.EqualTo(4), "a, b, c and d - a counted once");
            Assert.That(radius.ByVersion.Single(entry => entry.VersionNumber == 1).Subjects,
                Is.EqualTo(2));
            Assert.That(radius.ByVersion.Single(entry => entry.VersionNumber == 2).Subjects,
                Is.EqualTo(2), "the newly assigned guild and the granted one");
            Assert.That(radius.CurrentVersion, Is.EqualTo(2));
            Assert.That(radius.AppliesToEveryUnassignedSubject, Is.False);
        });
    }

    /// <summary>Billing holds no guild table, so for the instance's default plan the honest answer
    /// is "everybody who has never been assigned anything, and this many on top" rather than a
    /// confident number that is wrong by every guild on the instance.</summary>
    [Test]
    public async Task The_default_plan_says_it_reaches_every_unassigned_subject()
    {
        await _plans.CreateAsync(
            new CreatePlan("free", "Free", null,
                new Dictionary<string, string> { ["voice.max_participants"] = "15" },
                null, null, "The free tier."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var radius = await _plans.BlastRadiusAsync("free", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(radius.AppliesToEveryUnassignedSubject, Is.True);
            Assert.That(radius.TotalSubjects, Is.Zero, "nobody is pinned to it yet");
        });
    }

    // ── Validation an admin cannot bypass ────────────────────────────────────

    /// <summary>
    /// Moderation, AutoMod and voice channels are outside what any plan may withhold.
    /// </summary>
    [Test]
    public async Task A_plan_that_would_withhold_voice_by_arithmetic_is_refused()
    {
        foreach (var participants in new[] { "0", "1" })
        {
            var refusal = Assert.ThrowsAsync<PlanRefusedException>(
                () => _plans.CreateAsync(
                    new CreatePlan($"cheap{participants}", null, null,
                        new Dictionary<string, string> { ["voice.max_participants"] = participants },
                        null, null, "Trimming the free tier."),
                    Admin, CancellationToken.None));

            Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.WithholdsPlanIndependentModule));
        }

        // The floor, not a ban on small rooms: two people is a call.
        var (plan, _) = await _plans.CreateAsync(
            new CreatePlan("pair", null, null,
                new Dictionary<string, string> { ["voice.max_participants"] = "2" },
                null, null, "A deliberately tiny tier."),
            Admin, CancellationToken.None);

        Assert.That(plan.Name, Is.EqualTo("pair"));
    }

    [Test]
    public async Task An_edit_that_would_withhold_voice_leaves_the_plan_exactly_as_it_was()
    {
        await CreateProAsync();

        var refusal = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.EditAsync("pro",
                new EditPlan(ProValues(participants: "0"), 2900, "usd", "Saving money."),
                Admin, CancellationToken.None));

        _db.ChangeTracker.Clear();
        var plan = await _db.Plans.SingleAsync();
        var versions = await _db.PlanVersions.CountAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.WithholdsPlanIndependentModule));
            Assert.That(versions, Is.EqualTo(1));
            Assert.That(plan.CurrentVersionNumber, Is.EqualTo(1));
        });
    }

    [Test]
    public void An_unknown_entitlement_key_is_refused_and_names_the_real_ones()
    {
        var refusal = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.CreateAsync(
                new CreatePlan("magic", null, null,
                    new Dictionary<string, string> { ["guild.teleportation"] = "true" },
                    null, null, "Because they asked nicely."),
                Admin, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.UnknownEntitlementKey));
            Assert.That(refusal.Message, Does.Contain("voice.max_participants"));
        });
    }

    [Test]
    public void A_value_its_key_cannot_take_is_refused()
    {
        foreach (var (key, value) in new[]
                 {
                     ("voice.video_ceiling", "4k120"),
                     ("voice.max_participants", "lots"),
                     ("guild.vanity_url", "maybe"),
                     ("voice.max_participants", "-5"),
                 })
        {
            var refusal = Assert.ThrowsAsync<PlanRefusedException>(
                () => _plans.CreateAsync(
                    new CreatePlan("odd", null, null,
                        new Dictionary<string, string> { [key] = value },
                        null, null, "Typed by hand."),
                    Admin, CancellationToken.None));

            Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.InvalidEntitlementValue),
                $"'{value}' is not a value '{key}' can take");
        }
    }

    /// <summary>A pinned version is addressed as <c>name@number</c>, so a plan called <c>pro@2</c>
    /// would shadow version 2 of <c>pro</c> in the resolved catalogue.</summary>
    [Test]
    public void A_plan_name_that_would_shadow_a_pinned_version_is_refused()
    {
        var refusal = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.CreateAsync(
                new CreatePlan("pro@2", null, null, ProValues(), null, null, "Clever."),
                Admin, CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.InvalidPlanName));
    }

    [Test]
    public async Task Every_write_needs_a_reason()
    {
        await CreateProAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Assert.ThrowsAsync<PlanRefusedException>(
                    () => _plans.EditAsync("pro", new EditPlan(ProValues(), null, null, "  "),
                        Admin, CancellationToken.None))!.Code,
                Is.EqualTo(PlanErrorCodes.ReasonRequired));

            Assert.That(Assert.ThrowsAsync<PlanRefusedException>(
                    () => _plans.ActivateAsync("pro", 1, "", Admin, CancellationToken.None))!.Code,
                Is.EqualTo(PlanErrorCodes.ReasonRequired));

            Assert.That(Assert.ThrowsAsync<PlanRefusedException>(
                    () => _plans.AssignAsync(Subjects.Guild, "pro", null, "\t", Admin,
                        CancellationToken.None))!.Code,
                Is.EqualTo(PlanErrorCodes.ReasonRequired));

            Assert.That(Assert.ThrowsAsync<PlanRefusedException>(
                    () => _plans.ArchivePlanAsync("pro", "", Admin, CancellationToken.None))!.Code,
                Is.EqualTo(PlanErrorCodes.ReasonRequired));
        });
    }

    [Test]
    public async Task A_write_with_no_actor_is_refused()
    {
        await CreateProAsync();

        var refusal = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.EditAsync("pro", new EditPlan(ProValues(), null, null, "A reason."),
                "", CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.ActorRequired));
    }

    [Test]
    public void An_amount_with_no_currency_is_refused()
    {
        var refusal = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.CreateAsync(
                new CreatePlan("pro", null, null, ProValues(), 2900, null, "Priced."),
                Admin, CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.InvalidPrice));
    }

    // ── Announcements ────────────────────────────────────────────────────────

    /// <summary>Every subject on the plan, pinned ones included.</summary>
    [Test]
    public async Task An_edit_announces_every_subject_on_the_plan_and_advances_each_version()
    {
        await CreateProAsync();

        await _plans.AssignAsync(EntitlementSubject.ForGuild("gild_a"), "pro", null, "One.", Admin,
            CancellationToken.None);
        await _plans.AssignAsync(EntitlementSubject.ForGuild("gild_b"), "pro", null, "Two.", Admin,
            CancellationToken.None);
        await _db.SaveChangesAsync();

        var (_, announcements) = await _plans.EditAsync("pro",
            new EditPlan(ProValues("60"), 2900, "usd", "More room."), Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var versionOfA = await _versions.VersionAsync(
            EntitlementSubject.ForGuild("gild_a"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(announcements, Has.Count.EqualTo(2));
            Assert.That(announcements.Select(a => a.SubjectId), Is.EquivalentTo(new[] { "gild_a", "gild_b" }));
            Assert.That(announcements[0].Reason,
                Is.EqualTo(EntitlementsChangedReason.PlanVersionActivated));
            Assert.That(announcements[0].ChangedKeys, Does.Contain("voice.max_participants"));
            Assert.That(versionOfA, Is.EqualTo(2), "assigned once, then announced by the edit");
        });
    }

    // ── Nothing is ever deleted ──────────────────────────────────────────────

    [Test]
    public async Task A_plan_somebody_is_on_cannot_be_archived()
    {
        await CreateProAsync();
        await _plans.AssignAsync(Subjects.Guild, "pro", null, "Onboarded.", Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var refusal = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.ArchivePlanAsync("pro", "Retiring the tier.", Admin, CancellationToken.None));

        Assert.Multiple(async () =>
        {
            Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.PlanInUse));
            Assert.That((await _db.Plans.SingleAsync()).ArchivedAt, Is.Null);
        });
    }

    [Test]
    public async Task An_archived_plan_keeps_its_row_its_versions_and_the_reason_it_went()
    {
        await CreateProAsync();
        await _plans.EditAsync("pro", new EditPlan(ProValues("60"), 2900, "usd", "More room."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        await _plans.ArchivePlanAsync("pro", "Replaced by the 2027 tiers.", Admin, CancellationToken.None);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var plan = await _db.Plans.SingleAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await _db.Plans.CountAsync(), Is.EqualTo(1), "the row must never be deleted");
            Assert.That(await _db.PlanVersions.CountAsync(), Is.EqualTo(2));
            Assert.That(plan.ArchiveReason, Is.EqualTo("Replaced by the 2027 tiers."));
            Assert.That(plan.ArchivedBy, Is.EqualTo(Admin));
            Assert.That(plan.ArchivedAt, Is.EqualTo(Now));
        });
    }

    [Test]
    public async Task A_version_that_is_current_or_that_somebody_is_pinned_to_cannot_be_archived()
    {
        await CreateProAsync();
        await _plans.AssignAsync(Subjects.Guild, "pro", null, "Onboarded.", Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        await _plans.EditAsync("pro", new EditPlan(ProValues("60"), 2900, "usd", "More room."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var current = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.ArchiveVersionAsync("pro", 2, "Tidying up.", Admin, CancellationToken.None));

        var pinned = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.ArchiveVersionAsync("pro", 1, "Tidying up.", Admin, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(current!.Code, Is.EqualTo(PlanErrorCodes.VersionInUse));
            Assert.That(pinned!.Code, Is.EqualTo(PlanErrorCodes.VersionInUse));
            Assert.That(pinned.Message, Does.Contain("1 subject(s)"));
        });
    }

    /// <summary>Including one that differs only in case.</summary>
    [Test]
    public async Task A_second_plan_under_the_same_name_is_refused_whatever_its_capitalisation()
    {
        await CreateProAsync();

        foreach (var name in new[] { "pro", "Pro", "PRO", " pro " })
        {
            var refusal = Assert.ThrowsAsync<PlanRefusedException>(
                () => _plans.CreateAsync(
                    new CreatePlan(name, null, null, ProValues(), null, null, "Again."),
                    Admin, CancellationToken.None));

            Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.PlanExists), $"'{name}'");
        }
    }

    /// <summary>The point of moving the catalogue into the database: a plan created ten minutes ago
    /// is grantable, where a catalogue bound once at startup would refuse it until a redeploy.
    /// </summary>
    [Test]
    public async Task A_grant_can_name_a_plan_that_was_created_from_the_console()
    {
        await _plans.CreateAsync(
            new CreatePlan("enterprise", null, null, ProValues(), 9900, "usd", "A hand-sold tier."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var (grant, _) = await _grants.IssueAsync(
            new IssueGrant(SubjectKind.Guild, Subjects.Guild.Id, GrantKind.Plan, "enterprise", null,
                null, "Hand-onboarded customer.", GrantSource.Staff),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.That(grant.Plan, Is.EqualTo("enterprise"));
    }

    [Test]
    public void Assigning_a_subject_to_a_plan_that_does_not_exist_is_refused()
    {
        var refusal = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.AssignAsync(Subjects.Guild, "enterprise", null, "Onboarded.", Admin,
                CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.UnknownPlan));
    }

    [Test]
    public async Task Assigning_a_subject_to_a_version_that_does_not_exist_is_refused()
    {
        await CreateProAsync();

        var refusal = Assert.ThrowsAsync<PlanRefusedException>(
            () => _plans.AssignAsync(Subjects.Guild, "pro", 7, "Onboarded.", Admin,
                CancellationToken.None));

        Assert.That(refusal!.Code, Is.EqualTo(PlanErrorCodes.UnknownVersion));
    }
}
