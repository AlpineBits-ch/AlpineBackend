using Echo.Dtos.Entitlements;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Wire;
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
        EntitlementInstanceInfo instance, long version = 0, int ttlSeconds = 60) =>
        new(
            new EntitlementResolver([]),
            new FixedVersionProvider(version),
            instance,
            new EntitlementReadOptions { TtlSeconds = ttlSeconds },
            new FixedClock());

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
