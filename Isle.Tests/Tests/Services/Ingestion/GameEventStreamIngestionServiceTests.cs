using Isle.Api.Services.Ingestion;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Services.Ingestion;

/// <summary>
/// Covers <see cref="GameEventStreamIngestionService"/>'s two overrides: the damage pre-filter
/// (<c>IsRelevant</c>, which exists purely to keep the loudest feed off the DI container) and the
/// per-event-kind translation into contract events (<c>PublishAsync</c>'s switch). Both are
/// `protected override`, invoked via <see cref="ProtectedInvoke"/> - see
/// BridgeStreamIngestionServiceTests for coverage of the shared read loop itself.
/// </summary>
[TestFixture]
public class GameEventStreamIngestionServiceTests
{
    private GameEventStreamIngestionService _service = null!;
    private IMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = Substitute.For<IMessageBus>();
        _service = new GameEventStreamIngestionService(
            Substitute.For<IEventStream>(),
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<GameEventStreamIngestionService>.Instance);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    private bool IsRelevant(GameEvent e) => ProtectedInvoke.Invoke<bool>(_service, "IsRelevant", e);

    private Task Publish(GameEvent e) =>
        ProtectedInvoke.InvokeTaskAsync(_service, "PublishAsync", e, _bus, CancellationToken.None);

    // ── IsRelevant: the damage pre-filter ────────────────────────────────────

    [Test]
    public void IsRelevant_ValidPlayerOnPlayerDamage_IsTrue()
    {
        var e = new DamageDealtEvent { AttackerSteam = "attacker", VictimSteam = "victim", Damage = 5 };
        Assert.That(IsRelevant(e), Is.True);
    }

    [Test]
    public void IsRelevant_ZeroDamage_IsFalse()
    {
        var e = new DamageDealtEvent { AttackerSteam = "a", VictimSteam = "v", Damage = 0 };
        Assert.That(IsRelevant(e), Is.False);
    }

    [Test]
    public void IsRelevant_NegativeDamage_IsFalse()
    {
        var e = new DamageDealtEvent { AttackerSteam = "a", VictimSteam = "v", Damage = -1 };
        Assert.That(IsRelevant(e), Is.False);
    }

    [Test]
    public void IsRelevant_NoAttacker_IsFalse()
    {
        var e = new DamageDealtEvent { AttackerSteam = "", VictimSteam = "v", Damage = 5 };
        Assert.That(IsRelevant(e), Is.False);
    }

    [Test]
    public void IsRelevant_NoVictim_IsFalse()
    {
        var e = new DamageDealtEvent { AttackerSteam = "a", VictimSteam = "   ", Damage = 5 };
        Assert.That(IsRelevant(e), Is.False);
    }

    [Test]
    public void IsRelevant_SelfInflictedDamage_IsFalse()
    {
        var e = new DamageDealtEvent { AttackerSteam = "same", VictimSteam = "same", Damage = 5 };
        Assert.That(IsRelevant(e), Is.False);
    }

    [Test]
    public void IsRelevant_NonDamageEvent_IsAlwaysTrue()
    {
        Assert.That(IsRelevant(new JoinEvent { Steam = "s" }), Is.True);
        Assert.That(IsRelevant(new LeaveEvent { Steam = "s" }), Is.True);
    }

    // ── PublishAsync: per-event-kind translation ─────────────────────────────

    [Test]
    public async Task PublishAsync_JoinEvent_PublishesUserJoined()
    {
        await Publish(new JoinEvent { Steam = "steam_1" });

        await _bus.Received(1).PublishAsync(Arg.Is<UserJoinedIsleServerEvent>(e => e.SteamId == "steam_1"));
    }

    [Test]
    public async Task PublishAsync_LeaveEvent_PublishesUserLeft()
    {
        await Publish(new LeaveEvent { Steam = "steam_1" });

        await _bus.Received(1).PublishAsync(Arg.Is<UserLeftIsleServerEvent>(e => e.SteamId == "steam_1"));
    }

    [Test]
    public async Task PublishAsync_DeathEvent_PublishesUserDiedWithSpecies()
    {
        await Publish(new DeathEvent { Steam = "steam_1", Species = "Rex" });

        await _bus.Received(1).PublishAsync(Arg.Is<UserDiedOnIsleServerEvent>(e =>
            e.SteamId == "steam_1" && e.Species == "Rex"));
    }

    [Test]
    public async Task PublishAsync_KillfeedEvent_PublishesKillfeedReportedWithAllFields()
    {
        await Publish(new KillfeedEvent
        {
            KillerSteam = "killer",
            VictimSteam = "victim",
            VictimWeightKg = 1234.5,
            IdempotencyKey = "idem-1",
        });

        await _bus.Received(1).PublishAsync(Arg.Is<PlayerKillfeedReportedEvent>(e =>
            e.KillerSteamId == "killer" && e.VictimSteamId == "victim"
            && e.VictimWeightInKg == 1234.5 && e.IdempotencyKey == "idem-1"));
    }

    [Test]
    public async Task PublishAsync_DamageDealtEvent_PublishesPlayerDamagedWithAllFields()
    {
        await Publish(new DamageDealtEvent
        {
            AttackerSteam = "attacker",
            VictimSteam = "victim",
            Damage = 42.5,
            Swings = 3,
            Ts = 999,
        });

        await _bus.Received(1).PublishAsync(Arg.Is<PlayerDamagedEvent>(e =>
            e.AttackerSteamId == "attacker" && e.VictimSteamId == "victim"
            && e.Damage == 42.5 && e.Swings == 3 && e.OccurredAt == 999));
    }

    [Test]
    public async Task PublishAsync_UnknownBaseGameEvent_PublishesNothing()
    {
        await Publish(new GameEvent { Steam = "steam_1" });

        Assert.That(_bus.ReceivedCalls(), Is.Empty);
    }
}
