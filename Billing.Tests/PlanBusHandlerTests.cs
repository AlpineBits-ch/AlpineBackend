using Billing.Application.Bus.Consumers;
using Billing.Application.Services;
using Billing.Contracts.Bus.Request;
using Billing.Infrastructure.Persistence;
using Billing.Tests.Helpers;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;

namespace Billing.Tests;

/// <summary>What Billing answers when another service asks about plans.</summary>
[TestFixture]
public class PlanBusHandlerTests
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

    // ── The catalogue ────────────────────────────────────────────────────────

    /// <summary>Every live version under its <c>name@number</c> form and the current one under its
    /// bare name, which is exactly the shape <c>PlanCatalogue</c> looks up - the reason a pin could be
    /// expressed as a plan name and grandfathering needed no change to the shared library.</summary>
    [Test]
    public async Task The_catalogue_answer_carries_every_live_version_and_the_current_one()
    {
        await CreateProAndEditItAsync();

        var response = await GetPlanCatalogueHandler.Handle(
            new GetPlanCatalogueRequest(), _catalogue, Plans.Options(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(Participants(response, "pro@1"), Is.EqualTo("50"));
            Assert.That(Participants(response, "pro@2"), Is.EqualTo("75"));
            Assert.That(Participants(response, "pro"), Is.EqualTo("75"), "the bare name is current");
            Assert.That(response.Plans.Select(plan => plan.Name), Contains.Item("free"),
                "a configured plan the table has never held still travels");
        });
    }

    /// <summary>Which plan an unassigned subject is on is configured beside the plans themselves, and
    /// travels with them. Asking three other services to keep a matching copy of "the free plan is
    /// called free" in their own configuration is a rule that would be broken within a release.
    /// </summary>
    [Test]
    public async Task The_catalogue_answer_names_the_instances_default_for_each_kind_of_subject()
    {
        var response = await GetPlanCatalogueHandler.Handle(
            new GetPlanCatalogueRequest(), _catalogue, Plans.Options(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.DefaultGuildPlan, Is.EqualTo(Plans.Free));
            Assert.That(response.DefaultUserPlan, Is.Null,
                "an instance that configured no user default has none, and one must not be invented");
        });
    }

    /// <summary>Without this a pinned subject's plan renders as <c>pro@2</c> - a lookup key - on the
    /// one screen where a member reads what they are on.</summary>
    [Test]
    public async Task Every_version_carries_the_plans_display_name()
    {
        await _plans.CreateAsync(
            new CreatePlan("pro", "Pro", null,
                new Dictionary<string, string> { ["voice.max_participants"] = "50" },
                null, null, "One."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();
        _catalogue.Invalidate();

        var response = await GetPlanCatalogueHandler.Handle(
            new GetPlanCatalogueRequest(), _catalogue, Plans.Options(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(Find(response, "pro").DisplayName, Is.EqualTo("Pro"));
            Assert.That(Find(response, "pro@1").DisplayName, Is.EqualTo("Pro"));
        });
    }

    // ── The assignment ───────────────────────────────────────────────────────

    /// <summary>The pin is the point: an edit wrote version 2 and this subject stays on version 1
    /// until somebody moves them, and the services that enforce it have to be told which.</summary>
    [Test]
    public async Task A_pinned_subject_reports_the_version_they_are_pinned_to()
    {
        await CreateProAndEditItAsync();

        await _plans.AssignAsync(Subjects.Guild, "pro", 1, "Grandfathered.", Admin, CancellationToken.None);
        await _db.SaveChangesAsync();
        _catalogue.Invalidate();

        var response = await GetPlanAssignmentHandler.Handle(
            Request(Subjects.Guild), _catalogue, CancellationToken.None);

        Assert.That(response.PlanReference, Is.EqualTo("pro@1"));
    }

    [Test]
    public async Task A_subject_assigned_without_a_version_is_pinned_to_the_current_one()
    {
        await CreateProAndEditItAsync();

        await _plans.AssignAsync(Subjects.Guild, "pro", null, "Onboarded.", Admin, CancellationToken.None);
        await _db.SaveChangesAsync();
        _catalogue.Invalidate();

        var response = await GetPlanAssignmentHandler.Handle(
            Request(Subjects.Guild), _catalogue, CancellationToken.None);

        Assert.That(response.PlanReference, Is.EqualTo("pro@2"));
    }

    /// <summary>The normal answer for almost every subject, and deliberately not "the default plan":
    /// Billing answers what it was told, and the caller decides what an absence means. Anything else
    /// would put a row's worth of meaning behind a configuration value.</summary>
    [Test]
    public async Task An_unassigned_subject_has_no_assignment_rather_than_a_default_one()
    {
        await CreateProAndEditItAsync();

        var guild = await GetPlanAssignmentHandler.Handle(
            Request(Subjects.Guild), _catalogue, CancellationToken.None);
        var user = await GetPlanAssignmentHandler.Handle(
            Request(Subjects.User), _catalogue, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(guild.PlanReference, Is.Null);
            Assert.That(user.PlanReference, Is.Null);
        });
    }

    /// <summary>A guild and an account with the same id are different subjects.</summary>
    [Test]
    public async Task An_assignment_belongs_to_one_kind_of_subject()
    {
        await CreateProAndEditItAsync();

        await _plans.AssignAsync(
            new EntitlementSubject(SubjectKind.User, Subjects.Guild.Id), "pro", 1,
            "A user who happens to share an id.", Admin, CancellationToken.None);
        await _db.SaveChangesAsync();
        _catalogue.Invalidate();

        var asGuild = await GetPlanAssignmentHandler.Handle(
            Request(Subjects.Guild), _catalogue, CancellationToken.None);

        Assert.That(asGuild.PlanReference, Is.Null);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private async Task CreateProAndEditItAsync()
    {
        await _plans.CreateAsync(
            new CreatePlan("pro", null, null,
                new Dictionary<string, string> { ["voice.max_participants"] = "50" },
                null, null, "One."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();
        _catalogue.Invalidate();

        await _plans.EditAsync("pro",
            new EditPlan(new Dictionary<string, string> { ["voice.max_participants"] = "75" },
                null, null, "Two."),
            Admin, CancellationToken.None);
        await _db.SaveChangesAsync();
        _catalogue.Invalidate();
    }

    private static GetPlanAssignmentRequest Request(EntitlementSubject subject) =>
        new() { SubjectKind = subject.Kind, SubjectId = subject.Id };

    private static CataloguePlanDto Find(GetPlanCatalogueResponse response, string name) =>
        response.Plans.Single(plan => plan.Name == name);

    private static string Participants(GetPlanCatalogueResponse response, string name) =>
        Find(response, name).Values["voice.max_participants"];
}
