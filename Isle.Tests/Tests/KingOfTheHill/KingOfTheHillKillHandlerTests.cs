using Isle.Api.Handlers.KingOfTheHill;
using Isle.Api.Services.KingOfTheHill;
using Isle.Api.Services.Rewards;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.KingOfTheHill;

[TestFixture]
public class KingOfTheHillKillHandlerTests
{
    private TestIsleContext _context = null!;
    private KingOfTheHillMatchStateStore _stateStore = null!;
    private KingOfTheHillControlLedger _ledger = null!;
    private RewardGranter _rewards = null!;
    private KingOfTheHillAnnouncer _announcer = null!;
    private IBridgeClient _bridge = null!;

    private Isle.Domain.Aggregates.Player _killer = null!;
    private Isle.Domain.Aggregates.Player _victim = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = TestIsleContext.Create();
        _stateStore = new KingOfTheHillMatchStateStore(RedisTestFactory.Create(), NullLogger<KingOfTheHillMatchStateStore>.Instance);
        _ledger = new KingOfTheHillControlLedger(RedisTestFactory.Create(), NullLogger<KingOfTheHillControlLedger>.Instance);
        _bridge = BridgeTestFactory.CreateDefault();
        _rewards = new RewardGranter(_context, _bridge, NullLogger<RewardGranter>.Instance);

        var presence = new Isle.Api.Services.State.PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<Isle.Api.Services.State.PlayerPresenceManager>.Instance);
        _announcer = new KingOfTheHillAnnouncer(_bridge, NullLogger<KingOfTheHillAnnouncer>.Instance, presence, _context);

        _killer = TestData.Player("steam_killer");
        _victim = TestData.Player("steam_victim");
        _context.Players.AddRange(_killer, _victim);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private Task HandleAsync(string killerId, string victimId) =>
        KingOfTheHillKillHandler.Handle(
            new PlayerKillEvent { KilerId = killerId, VictimId = victimId },
            _stateStore, _ledger, _rewards, _announcer, _context, NullLogger<KingOfTheHillKillHandler>.Instance, CancellationToken.None);

    private async Task<long> KillerXpAsync() => (await _context.Players.AsNoTracking().FirstAsync(p => p.Id == _killer.Id)).Xp;

    [Test]
    public async Task Handle_SelfKill_GrantsNoReward()
    {
        await _stateStore.WriteAsync(new KothMatchState("def", "instance", DateTime.UtcNow, []));
        await _ledger.ApplyPresenceAsync("instance", [_killer.SteamId]);

        await HandleAsync(_killer.Id, _killer.Id);

        Assert.That(await KillerXpAsync(), Is.EqualTo(25000));
    }

    [Test]
    public async Task Handle_NoMatchRunning_GrantsNoReward()
    {
        await HandleAsync(_killer.Id, _victim.Id);

        Assert.That(await KillerXpAsync(), Is.EqualTo(25000));
    }

    [Test]
    public async Task Handle_MatchRunningButNoCurrentHolder_GrantsNoReward()
    {
        await _stateStore.WriteAsync(new KothMatchState("def", "instance", DateTime.UtcNow, []));
        // Ledger has no holder - nobody has been sole occupant yet.

        await HandleAsync(_killer.Id, _victim.Id);

        Assert.That(await KillerXpAsync(), Is.EqualTo(25000));
    }

    [Test]
    public async Task Handle_VictimIsNotTheCurrentHolder_GrantsNoReward()
    {
        await _stateStore.WriteAsync(new KothMatchState("def", "instance", DateTime.UtcNow, []));
        await _ledger.ApplyPresenceAsync("instance", ["steam_someone_else"]);

        await HandleAsync(_killer.Id, _victim.Id);

        Assert.That(await KillerXpAsync(), Is.EqualTo(25000));
    }

    [Test]
    public async Task Handle_VictimIsTheCurrentHolder_PaysTheKiller()
    {
        await _stateStore.WriteAsync(new KothMatchState("def", "instance", DateTime.UtcNow, []));
        await _ledger.ApplyPresenceAsync("instance", [_victim.SteamId]);

        await HandleAsync(_killer.Id, _victim.Id);

        Assert.That(await KillerXpAsync(), Is.EqualTo(25000 + 1500));
    }

    [Test]
    public async Task Handle_KillerIsNotARegisteredPlayer_GrantsNothingAndDoesNotThrow()
    {
        await _stateStore.WriteAsync(new KothMatchState("def", "instance", DateTime.UtcNow, []));
        await _ledger.ApplyPresenceAsync("instance", [_victim.SteamId]);

        Assert.DoesNotThrowAsync(() => HandleAsync("player_does_not_exist", _victim.Id));

        var victimXp = (await _context.Players.AsNoTracking().FirstAsync(p => p.Id == _victim.Id)).Xp;
        Assert.That(victimXp, Is.EqualTo(25000), "the victim themself must never be paid for their own death");
    }

    [Test]
    public async Task Handle_VictimIsTheCurrentHolder_WhispersTheKillerViaTheBridge()
    {
        await _stateStore.WriteAsync(new KothMatchState("def", "instance", DateTime.UtcNow, []));
        await _ledger.ApplyPresenceAsync("instance", [_victim.SteamId]);

        await HandleAsync(_killer.Id, _victim.Id);

        // The XP figure is formatted with "N0" against the current culture (varies by machine/CI -
        // e.g. a thousands separator isn't always a comma), so match on content, not exact digits.
        await _bridge.Received(1).DmAsync(
            Arg.Is<string>(text => text.Contains("Took the hill's holder down") && text.Contains("XP")),
            _killer.SteamId, Arg.Any<string?>(), Arg.Any<ChatMode>(), Arg.Any<CancellationToken>());
    }
}
