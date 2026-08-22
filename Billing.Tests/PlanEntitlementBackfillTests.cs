using Billing.Application.Services;
using Billing.Domain.Aggregates;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>
/// The startup backfill for entitlement keys a plan gained after it was seeded. This is the whole
/// reason <c>guild.public_listing</c> read false on a paid guild in production: the key was added
/// to configuration long after the database's first start, and the seeder returns on the first
/// existing plan.
/// </summary>
[TestFixture]
public class PlanEntitlementBackfillTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private MicroserviceContext _db = null!;
    private PlanService _plans = null!;

    [OneTimeSetUp]
    public Task StartDatabase() => PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task Reset()
    {
        await PostgresTestDatabase.ResetToEmptyAsync();

        _db = PostgresTestDatabase.CreateContext();
        await _db.Database.MigrateAsync();

        var catalogue = new PlanCatalogueService(_db, Plans.Catalogue());
        _plans = new PlanService(
            _db, catalogue, new EntitlementVersionService(_db), Plans.Options(), new TestClock(Now));
    }

    [TearDown]
    public async Task Dispose() => await _db.DisposeAsync();

    /// <summary>A plan exactly as the seeder leaves it, carrying only the keys that existed then.</summary>
    private async Task<Plan> SeedProAsync(Dictionary<string, string> values)
    {
        var plan = new Plan
        {
            Id = Plan.GenerateId(),
            Name = "pro",
            CurrentVersionNumber = 1,
            SeededFromConfiguration = true,
            CreatedBy = PlanSeeder.SystemActor,
        };

        _db.Plans.Add(plan);
        _db.PlanVersions.Add(new PlanVersion
        {
            Id = PlanVersion.GenerateId(),
            PlanId = plan.Id,
            VersionNumber = 1,
            ValuesJson = System.Text.Json.JsonSerializer.Serialize(values),
            PriceMinorUnits = 2900,
            Currency = "usd",
            Reason = PlanSeeder.SeedReason,
            CreatedBy = PlanSeeder.SystemActor,
        });

        await _db.SaveChangesAsync();
        return plan;
    }

    private static PlanCatalogue CatalogueWith(Dictionary<string, string> proValues) =>
        PlanCatalogue.FromOptions(new EntitlementPlanOptions
        {
            DefaultGuildPlan = "free",
            Plans = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro"] = proValues,
            },
        });

    private async Task<IReadOnlyDictionary<string, string>> CurrentValuesAsync(Plan plan)
    {
        var refreshed = await _db.Plans.AsNoTracking().SingleAsync(p => p.Id == plan.Id);
        var version = await _db.PlanVersions.AsNoTracking()
            .SingleAsync(v => v.PlanId == plan.Id && v.VersionNumber == refreshed.CurrentVersionNumber);

        return PlanCatalogueService.ReadValues(version.ValuesJson);
    }

    [Test]
    public async Task A_key_added_after_seeding_reaches_a_plan_that_was_already_planted()
    {
        var plan = await SeedProAsync(new Dictionary<string, string>
        {
            ["voice.max_participants"] = "75",
        });

        var filled = await PlanEntitlementBackfill.RunAsync(
            _db,
            CatalogueWith(new Dictionary<string, string>
            {
                ["voice.max_participants"] = "75",
                ["guild.public_listing"] = "true",
            }),
            _plans,
            null,
            CancellationToken.None);

        var values = await CurrentValuesAsync(plan);

        Assert.Multiple(() =>
        {
            Assert.That(filled, Is.EqualTo(1));
            Assert.That(values["guild.public_listing"], Is.EqualTo("true"));
        });
    }

    [Test]
    public async Task A_value_already_stored_is_left_alone()
    {
        // The operator turned it off from the console. Configuration is the seed, not the authority.
        var plan = await SeedProAsync(new Dictionary<string, string>
        {
            ["voice.max_participants"] = "75",
            ["guild.public_listing"] = "false",
        });

        await PlanEntitlementBackfill.RunAsync(
            _db,
            CatalogueWith(new Dictionary<string, string>
            {
                ["voice.max_participants"] = "10",
                ["guild.public_listing"] = "true",
            }),
            _plans,
            null,
            CancellationToken.None);

        var values = await CurrentValuesAsync(plan);

        Assert.Multiple(() =>
        {
            Assert.That(values["guild.public_listing"], Is.EqualTo("false"));
            Assert.That(values["voice.max_participants"], Is.EqualTo("75"));
        });
    }

    [Test]
    public async Task A_plan_the_operator_created_is_never_touched()
    {
        var plan = await SeedProAsync(new Dictionary<string, string>
        {
            ["voice.max_participants"] = "75",
        });

        plan.SeededFromConfiguration = false;
        await _db.SaveChangesAsync();

        var filled = await PlanEntitlementBackfill.RunAsync(
            _db,
            CatalogueWith(new Dictionary<string, string>
            {
                ["voice.max_participants"] = "75",
                ["guild.public_listing"] = "true",
            }),
            _plans,
            null,
            CancellationToken.None);

        var values = await CurrentValuesAsync(plan);

        Assert.Multiple(() =>
        {
            Assert.That(filled, Is.EqualTo(0));
            Assert.That(values.ContainsKey("guild.public_listing"), Is.False);
        });
    }

    [Test]
    public async Task A_second_run_changes_nothing()
    {
        var plan = await SeedProAsync(new Dictionary<string, string>
        {
            ["voice.max_participants"] = "75",
        });

        var catalogue = CatalogueWith(new Dictionary<string, string>
        {
            ["voice.max_participants"] = "75",
            ["guild.public_listing"] = "true",
        });

        await PlanEntitlementBackfill.RunAsync(_db, catalogue, _plans, null, CancellationToken.None);
        var afterFirst = (await _db.Plans.AsNoTracking().SingleAsync(p => p.Id == plan.Id)).CurrentVersionNumber;

        var filled = await PlanEntitlementBackfill.RunAsync(_db, catalogue, _plans, null, CancellationToken.None);
        var afterSecond = (await _db.Plans.AsNoTracking().SingleAsync(p => p.Id == plan.Id)).CurrentVersionNumber;

        Assert.Multiple(() =>
        {
            Assert.That(filled, Is.EqualTo(0));
            Assert.That(afterSecond, Is.EqualTo(afterFirst));
        });
    }
}
