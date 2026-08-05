using Isle.Api.Handlers.Quests;
using Isle.Api.Services.Quests;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Contracts.Events.Player;
using Isle.Contracts.Events.Quest;
using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Handlers.Quests;

[TestFixture]
public class BountyLifecycleHandlerTests
{
    private TestIsleContext _context = null!;
    private BountyRegistry _registry = null!;
    private BountyParticipantLedger _ledger = null!;
    private KillStreakTracker _streaks = null!;
    private WorldRosterCache _roster = null!;
    private IMessageBus _bus = null!;
    private BountyService _bounties = null!;
    private BountyDispatcher _dispatcher = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _registry = new BountyRegistry(RedisTestFactory.Create(), NullLogger<BountyRegistry>.Instance);
        _ledger = new BountyParticipantLedger(RedisTestFactory.Create(), NullLogger<BountyParticipantLedger>.Instance);
        _streaks = new KillStreakTracker(RedisTestFactory.Create(), NullLogger<KillStreakTracker>.Instance);
        _roster = new WorldRosterCache();
        _bus = Substitute.For<IMessageBus>();

        var bridge = BridgeTestFactory.CreateDefault();
        var rewards = new RewardGranter(_context, bridge, NullLogger<RewardGranter>.Instance);
        var presence = new PlayerPresenceManager(RedisTestFactory.Create(), NullLogger<PlayerPresenceManager>.Instance);
        var announcer = new QuestAnnouncer(bridge, NullLogger<QuestAnnouncer>.Instance, presence, _context);

        _bounties = new BountyService(
            _context, _registry, _ledger, _streaks, announcer, rewards, _roster,
            new RegionMap(), bridge, Substitute.For<ISkinStore>(), _bus, NullLogger<BountyService>.Instance);

        _dispatcher = new BountyDispatcher(_context, _bounties, _streaks, _bus, NullLogger<BountyDispatcher>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private async Task<Player> AddPlayerAsync(string steamId)
    {
        var player = TestData.Player(steamId);
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    private async Task AddBountyTemplateAsync()
    {
        var quest = new Quest { Id = Quest.GenerateId(), Name = "Bounty", Description = "", Type = QuestType.Bounty, Enabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Quests.Add(quest);
        await _context.SaveChangesAsync();
    }

    // --- BountyDispatcher.ResolveDeathForSteamAsync (also exercised via BountyLifecycleHandler.Handle(UserDiedOnIsleServerEvent)) ---

    [Test]
    public void ResolveDeathForSteam_UnknownSteamId_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _dispatcher.ResolveDeathForSteamAsync("steam_unknown", CancellationToken.None));
    }

    [Test]
    public async Task ResolveDeathForSteam_NoOpenBounty_ResetsStreakButSchedulesNothing()
    {
        var player = await AddPlayerAsync("steam_1");
        await _streaks.RegisterKillAsync(player.Id);

        await _dispatcher.ResolveDeathForSteamAsync(player.SteamId, CancellationToken.None);

        Assert.That(await _streaks.GetAsync(player.Id), Is.EqualTo(0));
        await _bus.DidNotReceive().PublishAsync(Arg.Any<ResolveBountyDeathEvent>(), Arg.Any<DeliveryOptions>());
    }

    [Test]
    public async Task ResolveDeathForSteam_OpenBounty_SchedulesTheDelayedResolution()
    {
        await AddBountyTemplateAsync();
        var player = await AddPlayerAsync("steam_1");
        var instance = await _bounties.StartAsync(player.Id, TimeSpan.FromMinutes(20));

        await _dispatcher.ResolveDeathForSteamAsync(player.SteamId, CancellationToken.None);

        await _bus.Received(1).PublishAsync(
            Arg.Is<ResolveBountyDeathEvent>(e => e.PlayerId == player.Id && e.QuestInstanceId == instance!.Id),
            Arg.Any<DeliveryOptions>());
    }

    [Test]
    public async Task HandleUserDied_DelegatesToTheDispatcher()
    {
        await AddBountyTemplateAsync();
        var player = await AddPlayerAsync("steam_1");
        var instance = await _bounties.StartAsync(player.Id, TimeSpan.FromMinutes(20));

        await BountyLifecycleHandler.Handle(new UserDiedOnIsleServerEvent { SteamId = player.SteamId }, _dispatcher, CancellationToken.None);

        await _bus.Received(1).PublishAsync(
            Arg.Is<ResolveBountyDeathEvent>(e => e.QuestInstanceId == instance!.Id),
            Arg.Any<DeliveryOptions>());
    }

    // --- BountyDispatcher.EndForSteamAsync (also exercised via BountyLifecycleHandler.Handle(UserLeftIsleServerEvent)) --------------

    [Test]
    public void EndForSteam_UnknownSteamId_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _dispatcher.EndForSteamAsync("steam_unknown", QuestInstanceState.Cancelled, CancellationToken.None));
    }

    [Test]
    public async Task EndForSteam_NoOpenBounty_JustResetsStreak()
    {
        var player = await AddPlayerAsync("steam_1");
        await _streaks.RegisterKillAsync(player.Id);

        await _dispatcher.EndForSteamAsync(player.SteamId, QuestInstanceState.Cancelled, CancellationToken.None);

        Assert.That(await _streaks.GetAsync(player.Id), Is.EqualTo(0));
    }

    [Test]
    public async Task EndForSteam_OpenBounty_ClosesItWithTheGivenState()
    {
        await AddBountyTemplateAsync();
        var player = await AddPlayerAsync("steam_1");
        var instance = await _bounties.StartAsync(player.Id, TimeSpan.FromMinutes(20));

        await _dispatcher.EndForSteamAsync(player.SteamId, QuestInstanceState.Cancelled, CancellationToken.None);

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance!.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Cancelled));
        Assert.That(await _registry.IsMarkedAsync(player.SteamId), Is.False);
    }

    [Test]
    public async Task HandleUserLeft_DelegatesToTheDispatcherAndCancelsTheBounty()
    {
        await AddBountyTemplateAsync();
        var player = await AddPlayerAsync("steam_1");
        var instance = await _bounties.StartAsync(player.Id, TimeSpan.FromMinutes(20));

        await BountyLifecycleHandler.Handle(new UserLeftIsleServerEvent { SteamId = player.SteamId }, _dispatcher, CancellationToken.None);

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance!.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Cancelled));
    }

    // --- BountyDeathResolutionHandler --------------------------------------------------------------------

    [Test]
    public void HandleResolveBountyDeath_NoOpenBounty_DoesNotThrow()
    {
        var @event = new ResolveBountyDeathEvent { PlayerId = "player_missing", QuestInstanceId = "qi_missing" };

        Assert.DoesNotThrowAsync(() => BountyDeathResolutionHandler.Handle(@event, _bounties, NullLogger<BountyDeathResolutionHandler>.Instance, CancellationToken.None));
    }

    [Test]
    public async Task HandleResolveBountyDeath_MatchingOpenBounty_ResolvesIt()
    {
        await AddBountyTemplateAsync();
        var player = await AddPlayerAsync("steam_1");
        var instance = await _bounties.StartAsync(player.Id, TimeSpan.FromMinutes(20));
        var @event = new ResolveBountyDeathEvent { PlayerId = player.Id, QuestInstanceId = instance!.Id };

        await BountyDeathResolutionHandler.Handle(@event, _bounties, NullLogger<BountyDeathResolutionHandler>.Instance, CancellationToken.None);

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == instance.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Completed));
    }

    [Test]
    public async Task HandleResolveBountyDeath_BountyWasReplacedSinceTheDeath_SkipsResolution()
    {
        await AddBountyTemplateAsync();
        var player = await AddPlayerAsync("steam_1");
        var staleInstance = await _bounties.StartAsync(player.Id, TimeSpan.FromMinutes(20));
        await _bounties.CancelForPlayerAsync(player.Id, QuestInstanceState.Cancelled);
        var freshInstance = await _bounties.StartAsync(player.Id, TimeSpan.FromMinutes(20));

        // A death event carrying the id of the bounty that was open when the player died, which has
        // since been replaced by a new one - must not resolve the new (unrelated) bounty.
        var @event = new ResolveBountyDeathEvent { PlayerId = player.Id, QuestInstanceId = staleInstance!.Id };

        await BountyDeathResolutionHandler.Handle(@event, _bounties, NullLogger<BountyDeathResolutionHandler>.Instance, CancellationToken.None);

        var updated = await _context.QuestInstances.AsNoTracking().FirstAsync(i => i.Id == freshInstance!.Id);
        Assert.That(updated.State, Is.EqualTo(QuestInstanceState.Active), "the replacement bounty must be untouched by a stale death");
    }
}
