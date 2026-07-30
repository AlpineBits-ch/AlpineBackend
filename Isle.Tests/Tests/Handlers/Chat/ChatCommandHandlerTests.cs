using Isle.Api.Chat;
using Isle.Api.Handlers.Chat;
using Isle.Api.Services.State;
using Isle.Contracts.Events.Chat;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Chat;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Wolverine;

namespace Isle.Tests.Tests.Handlers.Chat;

/// <summary>
/// <see cref="ChatCommandRegistry"/> eagerly instantiates every registered <c>!command</c> to learn
/// its name, so simply new-ing one up for a test requires the whole ~15-service dependency graph to
/// be resolvable. <see cref="ChatAutoMockServiceProvider"/> does that generically (explicit overrides win,
/// everything else auto-fakes), so these tests exercise the real registry/dispatch/cooldown/reply
/// pipeline rather than a hand-rolled stand-in for it.
/// </summary>
[TestFixture]
public class ChatCommandHandlerTests
{
    private TestIsleContext _context = null!;
    private IBridgeClient _bridge = null!;
    private IMessageBus _bus = null!;
    private IDistributedCache _cache = null!;
    private CommandCooldownService _cooldowns = null!;
    private ChatCommandRegistry _registry = null!;
    private ChatCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _bridge = BridgeTestFactory.CreateDefault(); // GetStatsAsync throws by default - exercises the "no live pawn" branch everywhere.
        _bus = Substitute.For<IMessageBus>();

        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        _cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _cooldowns = new CommandCooldownService(_cache);

        var provider = new ChatAutoMockServiceProvider(
            (typeof(Isle.Infrastructure.Persistence.MicroserviceContext), _context),
            (typeof(IBridgeClient), _bridge),
            (typeof(IMessageBus), _bus),
            (typeof(IConnectionMultiplexer), RedisTestFactory.Create()),
            (typeof(IDistributedCache), _cache));

        _registry = new ChatCommandRegistry(provider);
        _handler = new ChatCommandHandler(_registry, provider, _bridge, _cooldowns, NullLogger<ChatCommandHandler>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<Isle.Domain.Aggregates.Player> AddPlayerAsync(string steamId, bool isAdmin = false)
    {
        var player = TestData.Player(steamId);
        if (isAdmin) player.SetAdmin();
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    [Test]
    public async Task Handle_TextNotStartingWithBang_IsIgnored()
    {
        await AddPlayerAsync("steam-1");

        await _handler.Handle(new ChatMessageReceivedEvent { SteamId = "steam-1", Text = "hello everyone" }, CancellationToken.None);

        await _bridge.DidNotReceive().DmAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ChatMode>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnknownCommand_RepliesCommandNotFound()
    {
        await _handler.Handle(new ChatMessageReceivedEvent { SteamId = "steam-1", Text = "!bogus" }, CancellationToken.None);

        await _bridge.Received(1).DmAsync(
            Arg.Is<string>(s => s.Contains("bogus") && s.Contains("not found")),
            steam: "steam-1", sender: Arg.Any<string?>(), mode: ChatMode.Spatial, ct: Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_KnownCommand_UnregisteredPlayer_RepliesNothing()
    {
        await _handler.Handle(new ChatMessageReceivedEvent { SteamId = "steam-ghost", Text = "!id" }, CancellationToken.None);

        await _bridge.DidNotReceive().DmAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ChatMode>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AdminOnlyCommand_NonAdminPlayer_RepliesNotAllowed()
    {
        await AddPlayerAsync("steam-1", isAdmin: false);

        await _handler.Handle(new ChatMessageReceivedEvent { SteamId = "steam-1", Text = "!promote steam-2" }, CancellationToken.None);

        await _bridge.Received(1).DmAsync(
            Arg.Is<string>(s => s.Contains("not allowed")),
            steam: "steam-1", sender: Arg.Any<string?>(), mode: ChatMode.Spatial, ct: Arg.Any<CancellationToken>());
        await _bus.DidNotReceive().PublishAsync(Arg.Any<Isle.Contracts.Commands.ChangePlayerAdminStatusCommand>());
    }

    [Test]
    public async Task Handle_KnownCommand_Success_RepliesTheCommandResultAndStartsItsCooldown()
    {
        await AddPlayerAsync("steam-1");

        await _handler.Handle(new ChatMessageReceivedEvent { SteamId = "steam-1", Text = "!koth" }, CancellationToken.None);

        await _bridge.Received(1).DmAsync(
            Arg.Is<string>(s => s.Contains("King of the Hill")),
            steam: "steam-1", sender: Arg.Any<string?>(), mode: ChatMode.Spatial, ct: Arg.Any<CancellationToken>());

        var player = _context.Players.Single();
        var remaining = await _cooldowns.GetRemainingAsync(player.Id, "koth");
        Assert.That(remaining, Is.Not.Null, "!koth has a cooldown - a successful run must start it");
    }

    [Test]
    public async Task Handle_CommandStillOnCooldown_RepliesCooldownMessageInsteadOfRunningIt()
    {
        var player = await AddPlayerAsync("steam-1");
        await _cooldowns.StartAsync(player.Id, "koth", TimeSpan.FromSeconds(15));

        await _handler.Handle(new ChatMessageReceivedEvent { SteamId = "steam-1", Text = "!koth" }, CancellationToken.None);

        await _bridge.Received(1).DmAsync(
            Arg.Is<string>(s => s.Contains("koth") && s.Contains("cooldown")),
            steam: "steam-1", sender: Arg.Any<string?>(), mode: ChatMode.Spatial, ct: Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_CommandWithZeroCooldown_DoesNotTouchTheCooldownStore()
    {
        // !id (WhoAmICommand) has no cooldown override, so it defaults to TimeSpan.Zero.
        var player = await AddPlayerAsync("steam-1");

        await _handler.Handle(new ChatMessageReceivedEvent { SteamId = "steam-1", Text = "!id" }, CancellationToken.None);

        var remaining = await _cooldowns.GetRemainingAsync(player.Id, "id");
        Assert.That(remaining, Is.Null);
    }

    [Test]
    public async Task Handle_LiveDinoStatsAvailable_StillRunsTheCommandNormally()
    {
        // Covers the ApplyLiveDinoContextAsync happy path (stats resolve, vitals populate HealthData)
        // as opposed to the default BridgeTestFactory behavior of GetStatsAsync always throwing.
        _bridge.GetStatsAsync("steam-1", Arg.Any<CancellationToken>()).Returns(new StatsSnapshot
        {
            Steam = "steam-1",
            Species = "Deinosuchus",
            Growth = 1.0,
            Vitals = new Vitals { Hp = 100, HpMax = 100, Hunger = 50, Thirst = 50, Stamina = 100 },
        });
        await AddPlayerAsync("steam-1");

        await _handler.Handle(new ChatMessageReceivedEvent { SteamId = "steam-1", Text = "!koth" }, CancellationToken.None);

        await _bridge.Received(1).DmAsync(
            Arg.Is<string>(s => s.Contains("King of the Hill")),
            steam: "steam-1", sender: Arg.Any<string?>(), mode: ChatMode.Spatial, ct: Arg.Any<CancellationToken>());
    }
}
