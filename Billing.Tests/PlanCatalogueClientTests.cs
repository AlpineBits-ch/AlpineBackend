using Billing.Contracts.Bus.Request;
using Billing.Contracts.Clients;
using Billing.Tests.Helpers;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Microsoft.Extensions.DependencyInjection;

namespace Billing.Tests;

/// <summary>
/// The caller's half of the plan contracts: what a service that enforces plans does with Billing's
/// answers, and - the part worth the file - what it does when there are none.
/// </summary>
[TestFixture]
public class PlanCatalogueClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private IServiceScopeFactory _scopes = null!;
    private BillingClientOptions _options = null!;
    private TestClock _clock = null!;

    [SetUp]
    public void Setup()
    {
        // A real scope factory the seams never use.
        _scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _options = new BillingClientOptions();
        _clock = new TestClock(Now);
    }

    // ── The catalogue ────────────────────────────────────────────────────────

    /// <summary>The symptom this whole package exists for: five plans in <c>billing_db</c>, and an
    /// empty catalogue in every service that had to act on them.</summary>
    [Test]
    public async Task The_plans_billing_publishes_are_what_resolves()
    {
        var source = Catalogue(Response(("pro", "75")));

        var catalogue = await source.CurrentAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Participants(catalogue, "pro"), Is.EqualTo(75), "the table is the truth");
            Assert.That(catalogue.Find("free"), Is.Not.Null,
                "a plan only this build's configuration knows still resolves, so a rolling deploy "
                + "never resolves through a catalogue one plan short");
            Assert.That(source.DefaultGuildPlan, Is.EqualTo("free"),
                "the default rides with the catalogue because Billing is where plans are configured");
        });
    }

    [Test]
    public async Task A_versioned_plan_keeps_the_display_name_a_settings_screen_shows()
    {
        var response = Response(("pro", "75"));
        response.Plans.Add(new CataloguePlanDto
        {
            Name = "pro@1",
            DisplayName = "Pro",
            Values = new Dictionary<string, string> { ["voice.max_participants"] = "50" },
        });

        var catalogue = await Catalogue(response).CurrentAsync();

        Assert.That(catalogue.Find("pro@1")!.DisplayName, Is.EqualTo("Pro"),
            "pro@1 is an address, and is never something to render to a member");
    }

    [Test]
    public async Task A_fetched_catalogue_is_reused_until_its_window_lapses()
    {
        var source = Catalogue(Response(("pro", "75")));

        await source.CurrentAsync();
        await source.CurrentAsync();
        var withinWindow = source.Fetches;

        _clock.Advance(_options.CatalogueTtl + TimeSpan.FromSeconds(1));
        await source.CurrentAsync();

        Assert.Multiple(() =>
        {
            Assert.That(withinWindow, Is.EqualTo(1),
                "a broker hop per resolution is exactly what the shared library exists to avoid");
            Assert.That(source.Fetches, Is.EqualTo(2));
        });
    }

    /// <summary>An activation is the one event that moves the numbers, and waiting out the window
    /// after it is the difference between an edit taking effect and an edit appearing to do
    /// nothing.</summary>
    [Test]
    public async Task An_activation_sends_the_next_read_back_to_billing()
    {
        var source = Catalogue(Response(("pro", "75")));
        await source.CurrentAsync();

        source.Invalidate();
        source.Next = Response(("pro", "90"));

        Assert.That(Participants(await source.CurrentAsync(), "pro"), Is.EqualTo(90));
    }

    // ── When Billing is not there ────────────────────────────────────────────

    /// <summary>
    /// <b>The catalogue never empties.</b> It is the floor under every subject at once, so a failed
    /// read that produced no plans would not degrade one subject - it would move the whole instance in
    /// one step. Spec section 4.3 is explicit that a few hours of unpaid Pro is the cheaper incident.
    /// </summary>
    [Test]
    public async Task An_unreachable_billing_serves_the_last_catalogue_this_process_read()
    {
        var source = Catalogue(Response(("pro", "75")));
        await source.CurrentAsync();

        _clock.Advance(_options.CatalogueTtl + TimeSpan.FromSeconds(1));
        source.Fault = new TimeoutException("Billing did not answer.");

        var catalogue = await source.CurrentAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Participants(catalogue, "pro"), Is.EqualTo(75));
            Assert.That(source.DefaultGuildPlan, Is.EqualTo("free"),
                "the defaults are part of the last known answer and go stale with it, not before it");
        });
    }

    [Test]
    public async Task An_unreachable_billing_with_nothing_read_yet_serves_the_configured_catalogue()
    {
        var source = Catalogue(Response(("pro", "75")));
        source.Fault = new TimeoutException("Billing did not answer.");

        var catalogue = await source.CurrentAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Participants(catalogue, "pro"), Is.EqualTo(50), "the configured numbers");
            Assert.That(catalogue.Plans.Count(), Is.EqualTo(Configured().Plans.Count()));
            Assert.That(source.Revision, Is.EqualTo(PlanCatalogueRevision.Of(Configured())));
        });
    }

    /// <summary>Without the grace window every resolution in the process would re-attempt an
    /// unreachable service, and a billing outage would become a latency outage everywhere.</summary>
    [Test]
    public async Task An_outage_is_retried_on_the_grace_window_rather_than_on_every_read()
    {
        var source = Catalogue(Response(("pro", "75")));
        source.Fault = new TimeoutException("Billing did not answer.");

        await source.CurrentAsync();
        await source.CurrentAsync();
        var duringGrace = source.Fetches;

        _clock.Advance(_options.CatalogueOutageGrace + TimeSpan.FromSeconds(1));
        source.Fault = null;
        await source.CurrentAsync();

        Assert.Multiple(() =>
        {
            Assert.That(duringGrace, Is.EqualTo(1));
            Assert.That(source.Fetches, Is.EqualTo(2), "recovery is picked up in seconds, not in a TTL");
        });
    }

    // ── The revision, which is what invalidates every cache at once ──────────

    [Test]
    public async Task The_revision_moves_when_the_numbers_do_and_not_otherwise()
    {
        var source = Catalogue(Response(("pro", "75")));
        await source.CurrentAsync();
        var first = source.Revision;

        source.Invalidate();
        await source.CurrentAsync();
        var unchanged = source.Revision;

        source.Invalidate();
        source.Next = Response(("pro", "90"));
        await source.CurrentAsync();

        Assert.Multiple(() =>
        {
            Assert.That(unchanged, Is.EqualTo(first),
                "a re-read of the same plans must not roll every cache entry in every service");
            Assert.That(source.Revision, Is.Not.EqualTo(first),
                "and an edit must, because the subjects on the instance default have no assignment "
                + "row for an activation to announce them by");
        });
    }

    /// <summary>The rolling-deploy case.</summary>
    [Test]
    public async Task A_key_this_build_does_not_have_is_skipped_rather_than_fatal()
    {
        var response = new GetPlanCatalogueResponse
        {
            Plans =
            [
                new CataloguePlanDto
                {
                    Name = "pro",
                    Values = new Dictionary<string, string>
                    {
                        ["guild.teleportation"] = "true",
                        ["voice.max_participants"] = "not a number",
                        ["guild.emoji_slots"] = "500",
                    },
                },
            ],
        };

        var plan = (await Catalogue(response).CurrentAsync()).Find("pro")!;

        Assert.Multiple(() =>
        {
            Assert.That(plan.Values[EntitlementKeys.GuildEmojiSlots].AsNumber, Is.EqualTo(500));
            Assert.That(plan.Values, Has.Count.EqualTo(1));
        });
    }

    // ── The assignment ───────────────────────────────────────────────────────

    [Test]
    public async Task A_pinned_subject_resolves_through_the_version_they_were_pinned_to()
    {
        var assignment = Assignment(Catalogue(Response(("pro", "75"))), "pro@1");

        Assert.That(await assignment.PlanNameForAsync(Subjects.Guild, CancellationToken.None),
            Is.EqualTo("pro@1"), "grandfathering is only real outside Billing if the pin travels");
    }

    /// <summary>The product decision in this package.</summary>
    [Test]
    public async Task An_unassigned_subject_is_on_the_instances_default_plan()
    {
        var assignment = Assignment(Catalogue(Response(("pro", "75"))), pinned: null);

        Assert.That(await assignment.PlanNameForAsync(Subjects.Guild, CancellationToken.None),
            Is.EqualTo("free"));
    }

    [Test]
    public async Task An_unassigned_subject_falls_to_local_configuration_when_billing_names_no_default()
    {
        var response = Response(("pro", "75"));
        response.DefaultGuildPlan = null;
        response.DefaultUserPlan = null;

        var assignment = Assignment(Catalogue(response), pinned: null, configuredDefault: "plus");

        Assert.That(await assignment.PlanNameForAsync(Subjects.Guild, CancellationToken.None),
            Is.EqualTo("plus"), "which is the whole story on a config-only instance");
    }

    /// <summary>The rule that survives this package: a plan the instance never configured is not
    /// invented. Absent stays absent, and the client goes on saying so.</summary>
    [Test]
    public async Task An_unassigned_subject_on_an_instance_with_no_default_is_on_no_plan()
    {
        var response = Response(("pro", "75"));
        response.DefaultGuildPlan = null;
        response.DefaultUserPlan = null;

        var assignment = Assignment(Catalogue(response), pinned: null, configuredDefault: null);

        Assert.That(await assignment.PlanNameForAsync(Subjects.Guild, CancellationToken.None), Is.Null);
    }

    /// <summary>The negative case, and the one that would be silently wrong.</summary>
    [Test]
    public void An_unreachable_billing_throws_rather_than_reporting_no_assignment()
    {
        var assignment = Assignment(Catalogue(Response(("pro", "75"))), pinned: null);
        assignment.Fault = new TimeoutException("Billing did not answer.");

        Assert.That(async () => await assignment.PlanNameForAsync(Subjects.Guild, CancellationToken.None),
            Throws.InstanceOf<TimeoutException>());
    }

    [Test]
    public void An_unanswered_request_is_an_outage_and_not_an_empty_answer()
    {
        var assignment = Assignment(Catalogue(Response(("pro", "75"))), pinned: null);
        assignment.Answers = false;

        Assert.That(async () => await assignment.PlanNameForAsync(Subjects.Guild, CancellationToken.None),
            Throws.InstanceOf<InvalidOperationException>());
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private ScriptedCatalogueSource Catalogue(GetPlanCatalogueResponse response) =>
        new(_scopes, _options, Configured(), _clock) { Next = response };

    private ScriptedAssignment Assignment(
        ScriptedCatalogueSource catalogue, string? pinned, string? configuredDefault = "free") =>
        new(_scopes, _options, catalogue,
            new FixedPlanAssignment(new EntitlementPlanOptions { DefaultGuildPlan = configuredDefault }))
        {
            Pinned = pinned,
        };

    private static PlanCatalogue Configured() => Plans.Catalogue();

    private static long Participants(PlanCatalogue catalogue, string plan) =>
        catalogue.Find(plan)!.Values[EntitlementKeys.VoiceMaxParticipants].AsNumber;

    private static GetPlanCatalogueResponse Response(params (string Plan, string Participants)[] plans) =>
        new()
        {
            DefaultGuildPlan = "free",
            DefaultUserPlan = "user_free",
            Plans = plans.Select(plan => new CataloguePlanDto
            {
                Name = plan.Plan,
                Values = new Dictionary<string, string>
                {
                    ["voice.max_participants"] = plan.Participants,
                },
            }).ToList(),
        };

    /// <summary>The catalogue source with the broker replaced.</summary>
    private sealed class ScriptedCatalogueSource(
        IServiceScopeFactory scopes,
        BillingClientOptions options,
        PlanCatalogue configured,
        TimeProvider clock)
        : BusPlanCatalogueSource(scopes, options, configured, null, clock)
    {
        public GetPlanCatalogueResponse? Next { get; set; }

        /// <summary>Set to make Billing unreachable from the next fetch on.</summary>
        public Exception? Fault { get; set; }

        public int Fetches { get; private set; }

        protected override Task<GetPlanCatalogueResponse?> FetchAsync(CancellationToken cancellationToken)
        {
            Fetches++;

            return Fault is not null
                ? Task.FromException<GetPlanCatalogueResponse?>(Fault)
                : Task.FromResult(Next);
        }
    }

    private sealed class ScriptedAssignment(
        IServiceScopeFactory scopes,
        BillingClientOptions options,
        BusPlanCatalogueSource catalogue,
        FixedPlanAssignment configured)
        : BusPlanAssignment(scopes, options, catalogue, configured)
    {
        public string? Pinned { get; set; }

        public Exception? Fault { get; set; }

        /// <summary>False for the shape a broker timeout actually takes: no exception, no response.
        /// </summary>
        public bool Answers { get; set; } = true;

        protected override Task<GetPlanAssignmentResponse?> AssignmentAsync(
            EntitlementSubject subject, CancellationToken cancellationToken)
        {
            if (Fault is not null) return Task.FromException<GetPlanAssignmentResponse?>(Fault);

            return Task.FromResult(Answers
                ? new GetPlanAssignmentResponse { PlanReference = Pinned }
                : null);
        }
    }
}
