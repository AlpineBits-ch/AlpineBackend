using System.Text.Json;
using Billing.Application.Services;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>
/// How the configured plans and the stored ones compose, and what the seeder does the second time.
/// </summary>
[TestFixture]
public class PlanCatalogueTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private const string Admin = "user_admin";

    private MicroserviceContext _db = null!;
    private TestClock _clock = null!;
    private PlanCatalogueService _catalogue = null!;
    private PlanService _plans = null!;

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
        _plans = new PlanService(_db, _catalogue, new EntitlementVersionService(_db),
            Plans.Options(), _clock);
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    private Task<int> SeedAsync() => PlanSeeder.SeedAsync(
        _db, Plans.Catalogue(), new PlanSeedOptions
        {
            PlanSeed = new Dictionary<string, PlanSeedEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro"] = new() { DisplayName = "Pro", PriceMinorUnits = 2900, Currency = "USD" },
            },
        }, _clock, null);

    // ── Fallback ─────────────────────────────────────────────────────────────

    /// <summary>The config-only path, which is every instance until somebody opens the console and
    /// every self-hosted one forever. No rows means the configured catalogue, unchanged.</summary>
    [Test]
    public async Task With_no_rows_the_catalogue_is_exactly_the_configured_one()
    {
        var catalogue = await _catalogue.CurrentAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(catalogue, Is.SameAs(_catalogue.Configured));
            Assert.That(catalogue.Find("pro")!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber,
                Is.EqualTo(50));
        });
    }

    [Test]
    public async Task A_stored_plan_wins_over_the_configured_one_of_the_same_name()
    {
        await _plans.CreateAsync(
            new CreatePlan("pro", null, null,
                new Dictionary<string, string> { ["voice.max_participants"] = "75" },
                null, null, "The real number."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var catalogue = await _catalogue.CurrentAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(catalogue.Find("pro")!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber,
                Is.EqualTo(75), "the table is the truth for a name it holds");
            Assert.That(catalogue.Find("free"), Is.Not.Null,
                "a configured plan the table has never held still resolves");
        });
    }

    /// <summary>Archiving is a decision somebody made and recorded.</summary>
    [Test]
    public async Task An_archived_plan_is_not_resurrected_by_configuration()
    {
        await _plans.CreateAsync(
            new CreatePlan("plus", null, null,
                new Dictionary<string, string> { ["voice.max_participants"] = "35" },
                null, null, "The middle tier."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        await _plans.ArchivePlanAsync("plus", "Folded into Pro.", Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var catalogue = await _catalogue.CurrentAsync(CancellationToken.None);

        Assert.That(catalogue.Find("plus"), Is.Null);
    }

    [Test]
    public async Task Every_live_version_is_addressable_as_name_at_number()
    {
        await _plans.CreateAsync(
            new CreatePlan("pro", null, null,
                new Dictionary<string, string> { ["voice.max_participants"] = "50" },
                null, null, "One."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        await _plans.EditAsync("pro",
            new EditPlan(new Dictionary<string, string> { ["voice.max_participants"] = "75" },
                null, null, "Two."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var catalogue = await _catalogue.CurrentAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(catalogue.Find("pro@1")!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber,
                Is.EqualTo(50));
            Assert.That(catalogue.Find("pro@2")!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber,
                Is.EqualTo(75));
            Assert.That(catalogue.Find("pro")!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber,
                Is.EqualTo(75), "the bare name is whatever is current");
        });
    }

    /// <summary>The read path is deliberately the opposite of the write path: a key the catalogue no
    /// longer knows is skipped, because throwing here would deny every subject on the plan the rest
    /// of what it says.</summary>
    [Test]
    public async Task A_stored_value_the_key_catalogue_no_longer_knows_is_skipped()
    {
        var plan = new Plan
        {
            Id = Plan.GenerateId(),
            Name = "legacy",
            CurrentVersionNumber = 1,
            CreatedBy = Admin,
        };

        _db.Plans.Add(plan);
        _db.PlanVersions.Add(new PlanVersion
        {
            Id = PlanVersion.GenerateId(),
            PlanId = plan.Id,
            VersionNumber = 1,
            ValuesJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["guild.teleportation"] = "true",
                ["voice.max_participants"] = "not a number",
                ["guild.emoji_slots"] = "42",
            }),
            Reason = "Written before the key was removed.",
            CreatedBy = Admin,
        });

        await _db.SaveChangesAsync();

        var resolved = (await _catalogue.CurrentAsync(CancellationToken.None)).Find("legacy");

        Assert.Multiple(() =>
        {
            Assert.That(resolved!.Values[EntitlementKeys.GuildEmojiSlots].AsNumber, Is.EqualTo(42));
            Assert.That(resolved.Values, Has.Count.EqualTo(1),
                "the unknown key and the unparseable value are dropped, not thrown on");
        });
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Seeding_plants_the_configured_plans_with_their_names_and_prices()
    {
        var planted = await SeedAsync();

        var pro = await _db.Plans.SingleAsync(plan => plan.Name == "pro");
        var version = await _db.PlanVersions.SingleAsync(row => row.PlanId == pro.Id);

        Assert.Multiple(() =>
        {
            Assert.That(planted, Is.EqualTo(3));
            Assert.That(pro.SeededFromConfiguration, Is.True,
                "the console has to be able to say these are proposed defaults rather than decisions");
            Assert.That(pro.DisplayName, Is.EqualTo("Pro"));
            Assert.That(pro.CreatedBy, Is.EqualTo(PlanSeeder.SystemActor));
            Assert.That(version.PriceMinorUnits, Is.EqualTo(2900));
            Assert.That(version.Currency, Is.EqualTo("usd"), "normalised the way Stripe wants it");
            Assert.That(PlanCatalogueService.ReadValues(version.ValuesJson)["voice.max_participants"],
                Is.EqualTo("50"));
        });
    }

    /// <summary>
    /// The branch a fresh database hides: a start where rows already exist and the owner has
    /// changed them.
    /// </summary>
    [Test]
    public async Task Seeding_a_second_time_leaves_an_edited_catalogue_completely_alone()
    {
        await SeedAsync();
        _catalogue.Invalidate();

        await _plans.EditAsync("pro",
            new EditPlan(new Dictionary<string, string> { ["voice.max_participants"] = "75" },
                4900, "usd", "The owner's real numbers."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var planted = await SeedAsync();
        _catalogue.Invalidate();

        var catalogue = await _catalogue.CurrentAsync(CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(planted, Is.Zero, "a seeded catalogue is never re-seeded");
            Assert.That(await _db.Plans.CountAsync(), Is.EqualTo(3));
            Assert.That(catalogue.Find("pro")!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber,
                Is.EqualTo(75), "a restart must not put the configured defaults back over an edit");
        });
    }

    [Test]
    public async Task Seeding_does_not_bring_back_a_plan_that_was_archived()
    {
        await SeedAsync();
        _catalogue.Invalidate();

        await _plans.ArchivePlanAsync("plus", "Folded into Pro.", Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        await SeedAsync();
        _catalogue.Invalidate();

        var plus = await _db.Plans.SingleAsync(plan => plan.Name == "plus");
        var catalogue = await _catalogue.CurrentAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(plus.ArchivedAt, Is.Not.Null);
            Assert.That(catalogue.Find("plus"), Is.Null);
        });
    }

    /// <summary>A configuration key that could not be a plan name is skipped rather than thrown on.
    /// Refusing to start over one typo in a section that is only a seed is the worse failure.
    /// </summary>
    [Test]
    public async Task A_configured_plan_whose_name_could_not_be_one_is_skipped_rather_than_fatal()
    {
        var configured = PlanCatalogue.FromOptions(new EntitlementPlanOptions
        {
            Plans = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro@2"] = new() { ["voice.max_participants"] = "50" },
                ["pro"] = new() { ["voice.max_participants"] = "50" },
            },
        });

        var planted = await PlanSeeder.SeedAsync(_db, configured, new PlanSeedOptions(), _clock, null);

        Assert.Multiple(async () =>
        {
            Assert.That(planted, Is.EqualTo(1));
            Assert.That((await _db.Plans.SingleAsync()).Name, Is.EqualTo("pro"));
        });
    }

    [Test]
    public async Task An_instance_with_no_configured_plans_seeds_nothing_and_still_works()
    {
        var planted = await PlanSeeder.SeedAsync(
            _db, PlanCatalogue.Empty, new PlanSeedOptions(), _clock, null);

        var catalogue = await _catalogue.CurrentAsync(CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(planted, Is.Zero);
            Assert.That(await _db.Plans.CountAsync(), Is.Zero);
            Assert.That(catalogue, Is.SameAs(_catalogue.Configured));
        });
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    /// <summary>The same guard <c>GrantMigrationTests</c> puts on the grant table: these enums are
    /// text, because a Postgres enum type cannot gain a label without a migration and a service
    /// whose C# enum has a member the database does not know crashes at startup.</summary>
    [Test]
    public async Task The_plan_tables_store_their_enums_as_text()
    {
        var action = await PostgresTestDatabase.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = 'plan_audit_entries' AND column_name = 'action'");

        var subjectKind = await PostgresTestDatabase.ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns "
            + "WHERE table_name = 'plan_assignments' AND column_name = 'subject_kind'");

        var enumTypes = await PostgresTestDatabase.ScalarAsync<long>(
            "SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace "
            + "WHERE t.typtype = 'e' AND n.nspname = 'public'");

        Assert.Multiple(() =>
        {
            Assert.That(action, Is.EqualTo("text"));
            Assert.That(subjectKind, Is.EqualTo("text"));
            Assert.That(enumTypes, Is.Zero);
        });
    }

    /// <summary>A subject on two plans would make "which numbers apply to this guild" depend on
    /// which row a query read first, so the database refuses it rather than trusting every write
    /// path to remember.</summary>
    [Test]
    public async Task A_subject_cannot_hold_two_plan_assignments()
    {
        await _plans.CreateAsync(
            new CreatePlan("pro", null, null,
                new Dictionary<string, string> { ["voice.max_participants"] = "50" },
                null, null, "One."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();

        var plan = await _db.Plans.SingleAsync();

        _db.PlanAssignments.Add(NewAssignment(plan.Id));
        _db.PlanAssignments.Add(NewAssignment(plan.Id));

        Assert.That(async () => await _db.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    private static PlanAssignment NewAssignment(string planId) => new()
    {
        Id = PlanAssignment.GenerateId(),
        SubjectKind = SubjectKind.Guild,
        SubjectId = Subjects.Guild.Id,
        PlanId = planId,
        VersionNumber = 1,
        AssignedBy = Admin,
        Reason = "Onboarded.",
        AssignedAt = Now,
    };
}
