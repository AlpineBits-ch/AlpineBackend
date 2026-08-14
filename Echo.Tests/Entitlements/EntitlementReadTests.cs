using Echo.Dtos.Entitlements;
using Echo.Entitlements;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echo.Tests.Entitlements;

/// <summary>
/// The gateway's half of the entitlement contract: what the read endpoints add to a resolved set,
/// and who the change event reaches.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EntitlementReadTests
{
    private const string UserId = "user-1";
    private const string GuildId = "guild-1";

    // ── The instance, as the client reads it ─────────────────────────────────

    /// <summary>
    /// The self-hosting case, which is the default and the one a wrong answer is most visible in: a
    /// self-hoster who is shown an upgrade button has hit a paywall on a product nobody is charging
    /// them for.
    /// </summary>
    [Test]
    public async Task A_self_hosted_instance_offers_no_upgrade_on_either_subject()
    {
        var builder = Builder(new EntitlementInstanceInfo("selfhost", UpgradesAvailable: false));

        var user = await builder.ForUserAsync(UserId);
        var guild = await builder.ForGuildAsync(GuildId, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(user.LicenseMode, Is.EqualTo("selfhost"));
            Assert.That(user.UpgradesAvailable, Is.False);
            Assert.That(user.Remedy, Is.EqualTo(EntitlementRemedyCodes.None));
            Assert.That(user.ActorCanRemedy, Is.False);
            Assert.That(guild.Remedy, Is.EqualTo(EntitlementRemedyCodes.None));
            Assert.That(guild.ActorCanRemedy, Is.False,
                "holding ManageGuild does not make a button appear for a service that is not deployed");
        });
    }

    /// <summary>Hosted, but with nothing configured that could take money yet.</summary>
    [Test]
    public async Task A_hosted_instance_with_no_billing_configured_still_sells_nothing()
    {
        var snapshot = await Builder(new EntitlementInstanceInfo("hosted", UpgradesAvailable: false))
            .ForGuildAsync(GuildId, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.LicenseMode, Is.EqualTo("hosted"));
            Assert.That(snapshot.UpgradesAvailable, Is.False);
            Assert.That(snapshot.Remedy, Is.EqualTo(EntitlementRemedyCodes.None));
        });
    }

    [Test]
    public async Task A_guild_snapshot_says_whether_this_caller_could_buy_the_upgrade()
    {
        var builder = Builder(new EntitlementInstanceInfo("hosted", UpgradesAvailable: true));

        var owner = await builder.ForGuildAsync(GuildId, actorCanManageGuild: true);
        var member = await builder.ForGuildAsync(GuildId, actorCanManageGuild: false);

        Assert.Multiple(() =>
        {
            Assert.That(owner.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeGuild));
            Assert.That(owner.ActorCanRemedy, Is.True);
            Assert.That(member.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeGuild),
                "the remedy is the same fact for everyone; who can perform it is not");
            Assert.That(member.ActorCanRemedy, Is.False);
        });
    }

    [Test]
    public async Task A_snapshot_echoes_its_subject_its_version_and_its_ttl()
    {
        var snapshot = await Builder(
                new EntitlementInstanceInfo("hosted", UpgradesAvailable: true),
                version: 42,
                ttlSeconds: 30)
            .ForUserAsync(UserId);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Subject.Kind, Is.EqualTo("user"));
            Assert.That(snapshot.Subject.Id, Is.EqualTo(UserId));
            Assert.That(snapshot.Version, Is.EqualTo(42));
            Assert.That(snapshot.TtlSeconds, Is.EqualTo(30));
            Assert.That(snapshot.ResolvedAt, Is.EqualTo(DateTimeOffset.UnixEpoch));
        });
    }

    /// <summary>The shipped default has to be short enough to sit under any resolver cache backstop,
    /// because a client that caches longer than the server defeats the self-healing the backstop
    /// exists to provide.</summary>
    [Test]
    public void The_default_client_ttl_is_shorter_than_any_plausible_server_backstop()
    {
        Assert.That(new EntitlementReadOptions().TtlSeconds, Is.InRange(1, 300));
    }

    // ── Which plan the numbers came from ─────────────────────────────────────

    /// <summary>The question the settings screen asks and nothing on this payload could answer: the
    /// snapshot carried values and provenance-free ceilings, but no plan, so no client could say
    /// "you are on Plus" without inventing the name itself.</summary>
    [Test]
    public async Task A_hosted_snapshot_names_the_plan_the_subject_is_on()
    {
        var builder = Builder(
            new EntitlementInstanceInfo("hosted", UpgradesAvailable: true),
            assignment: new ScriptedAssignment(guildPlan: "plus", userPlan: "free"),
            plans: Configured());

        var guild = await builder.ForGuildAsync(GuildId, actorCanManageGuild: true);
        var user = await builder.ForUserAsync(UserId);

        Assert.Multiple(() =>
        {
            Assert.That(guild.Plan!.Name, Is.EqualTo("plus"));
            Assert.That(guild.Plan.DisplayName, Is.EqualTo("Venta Plus"));
            Assert.That(user.Plan!.Name, Is.EqualTo("free"),
                "a guild's plan and its members' are different bundles resolved from different sides");
        });
    }

    /// <summary>A subject grandfathered onto an older version of a plan is on that version, and the
    /// snapshot has to say so: the current version's numbers are not the ones they hold.</summary>
    [Test]
    public async Task A_pinned_subject_reports_the_version_it_is_pinned_to()
    {
        var snapshot = await Builder(
                new EntitlementInstanceInfo("hosted", UpgradesAvailable: true),
                assignment: new ScriptedAssignment(guildPlan: "pro@1", userPlan: null),
                plans: Versioned())
            .ForGuildAsync(GuildId, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Plan!.Name, Is.EqualTo("pro"));
            Assert.That(snapshot.Plan.Version, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// <b>Self-hosting names no plan, whatever is configured.</b> The license source short-circuits
    /// above every other source there, so every key is at maximum regardless of what any plan says -
    /// and naming a plan that is deciding nothing is a paywall-shaped sentence on the one instance
    /// nobody is being charged for.
    /// </summary>
    [Test]
    public async Task A_self_hosted_instance_names_no_plan_even_with_one_configured()
    {
        var snapshot = await Builder(
                new EntitlementInstanceInfo("selfhost", UpgradesAvailable: false),
                assignment: new ScriptedAssignment(guildPlan: "plus", userPlan: "free"),
                plans: Configured())
            .ForGuildAsync(GuildId, actorCanManageGuild: true);

        Assert.That(snapshot.Plan, Is.Null);
    }

    /// <summary>The other honest absence: a hosted instance that has defined no plans resolves every
    /// key to its catalogue default, which is a real state and not a Free tier.</summary>
    [Test]
    public async Task An_instance_with_no_plans_configured_names_no_plan()
    {
        var withNothingRegistered = await Builder(
                new EntitlementInstanceInfo("hosted", UpgradesAvailable: true))
            .ForGuildAsync(GuildId, actorCanManageGuild: true);

        var withAnEmptyCatalogue = await Builder(
                new EntitlementInstanceInfo("hosted", UpgradesAvailable: true),
                assignment: new ScriptedAssignment(guildPlan: "plus", userPlan: null),
                plans: PlanCatalogue.Empty)
            .ForGuildAsync(GuildId, actorCanManageGuild: true);

        Assert.Multiple(() =>
        {
            Assert.That(withNothingRegistered.Plan, Is.Null);
            Assert.That(withAnEmptyCatalogue.Plan, Is.Null,
                "a plan name the catalogue cannot resolve gave the subject no values either");
        });
    }

    /// <summary>The plan sources are optional constructor parameters, and the container has to fill
    /// them from what <c>AddEntitlements</c> registered rather than fall to the defaults. A
    /// regression here is silent: every snapshot would simply stop naming a plan, and no other test
    /// would notice.</summary>
    [Test]
    public async Task The_container_supplies_the_plan_sources_the_gateway_registers()
    {
        var services = new ServiceCollection()
            .AddEntitlements(options =>
            {
                options.DefaultGuildPlan = "plus";
                options.Plans["plus"] = new Dictionary<string, string>();
                options.PlanDisplayNames["plus"] = "Venta Plus";
            });

        services.AddSingleton<IEntitlementVersionProvider, StaticEntitlementVersionProvider>();
        services.AddSingleton(new EntitlementInstanceInfo("hosted", UpgradesAvailable: true));
        services.AddSingleton(new EntitlementReadOptions());
        services.AddSingleton<EntitlementSnapshotBuilder>();

        await using var provider = services.BuildServiceProvider();

        var snapshot = await provider.GetRequiredService<EntitlementSnapshotBuilder>()
            .ForGuildAsync(GuildId, actorCanManageGuild: true);

        Assert.That(snapshot.Plan!.DisplayName, Is.EqualTo("Venta Plus"));
    }

    // ── The publishable key ──────────────────────────────────────────────────

    [Test]
    public async Task The_publishable_key_rides_the_snapshot_when_the_instance_has_one()
    {
        var withKey = await Builder(
                new EntitlementInstanceInfo("hosted", UpgradesAvailable: true, StripePublishableKey: "pk_live_1"))
            .ForUserAsync(UserId);

        var without = await Builder(new EntitlementInstanceInfo("hosted", UpgradesAvailable: true))
            .ForUserAsync(UserId);

        Assert.Multiple(() =>
        {
            Assert.That(withKey.StripePublishableKey, Is.EqualTo("pk_live_1"));
            Assert.That(without.StripePublishableKey, Is.Null,
                "absent is the normal case and must not be an error");
        });
    }

    // ── The push, and who hears it ───────────────────────────────────────────

    [Test]
    public async Task A_user_change_reaches_that_user_and_nobody_else()
    {
        var hub = new FakeEntitlementsHubContext();
        await Notifier(hub).NotifyUserAsync(UserId, 7, ["user.max_devices"]);

        var sent = hub.HubClients.Sent.Single();

        Assert.Multiple(() =>
        {
            Assert.That(sent.Recipients, Is.EqualTo(new[] { UserId }));
            Assert.That(sent.Method, Is.EqualTo(EntitlementRealtimeEvents.Changed));
            Assert.That(((EntitlementsChangedDto)sent.Args[0]!).SubjectKind, Is.EqualTo("user"));
            Assert.That(((EntitlementsChangedDto)sent.Args[0]!).Version, Is.EqualTo(7));
        });
    }

    [Test]
    public async Task A_guild_change_reaches_the_members_it_was_given()
    {
        var hub = new FakeEntitlementsHubContext();
        await Notifier(hub).NotifyGuildAsync(GuildId, ["user-1", "user-2"], 9);

        var sent = hub.HubClients.Sent.Single();
        var payload = (EntitlementsChangedDto)sent.Args[0]!;

        Assert.Multiple(() =>
        {
            Assert.That(sent.Recipients, Is.EqualTo(new[] { "user-1", "user-2" }));
            Assert.That(payload.SubjectId, Is.EqualTo(GuildId));
            Assert.That(payload.ChangedKeys, Is.Empty);
        });
    }

    /// <summary>The negative case that matters.</summary>
    [Test]
    public async Task A_guild_change_with_no_recipients_sends_nothing()
    {
        var hub = new FakeEntitlementsHubContext();
        await Notifier(hub).NotifyGuildAsync(GuildId, [], 9);

        Assert.That(hub.HubClients.Sent, Is.Empty);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static EntitlementSnapshotBuilder Builder(
        EntitlementInstanceInfo instance,
        long version = 0,
        int ttlSeconds = 60,
        IPlanAssignment? assignment = null,
        PlanCatalogue? plans = null) =>
        new(
            new EntitlementResolver([]),
            new FixedVersionProvider(version),
            instance,
            new EntitlementReadOptions { TtlSeconds = ttlSeconds },
            new FixedClock(),
            assignment,
            plans);

    private static PlanCatalogue Configured() =>
        PlanCatalogue.FromOptions(new EntitlementPlanOptions
        {
            Plans =
            {
                ["free"] = new Dictionary<string, string>(),
                ["plus"] = new Dictionary<string, string>(),
            },
            PlanDisplayNames = { ["plus"] = "Venta Plus" },
        });

    /// <summary>A Billing-shaped catalogue, where every live version is addressable as
    /// <c>name@number</c> and the current one is also under its bare name.</summary>
    private static PlanCatalogue Versioned() =>
        new([
            new PlanDefinition("pro", new Dictionary<EntitlementKey, EntitlementValue>(), "Pro"),
            new PlanDefinition("pro@1", new Dictionary<EntitlementKey, EntitlementValue>()),
            new PlanDefinition("pro@2", new Dictionary<EntitlementKey, EntitlementValue>()),
        ]);

    private sealed class ScriptedAssignment(string? guildPlan, string? userPlan) : IPlanAssignment
    {
        public ValueTask<string?> PlanNameForAsync(
            EntitlementSubject subject, CancellationToken cancellationToken) =>
            ValueTask.FromResult(subject.Kind == SubjectKind.Guild ? guildPlan : userPlan);
    }

    private static EntitlementsChangeNotifier Notifier(FakeEntitlementsHubContext hub) =>
        new(hub, NullLogger<EntitlementsChangeNotifier>.Instance);

    private sealed class FixedVersionProvider(long version) : IEntitlementVersionProvider
    {
        public ValueTask<long> VersionAsync(EntitlementSubject subject, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(version);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
