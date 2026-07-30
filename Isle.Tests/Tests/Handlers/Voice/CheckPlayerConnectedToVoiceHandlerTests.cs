using Isle.Api.Handlers.Voice;
using Isle.Api.Services.Rcon;
using Isle.Api.Services.State;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TheIsleEvrimaRconClient;
using Wolverine;

namespace Isle.Tests.Tests.Handlers.Voice;

[TestFixture]
public class CheckPlayerConnectedToVoiceHandlerTests
{
    private TestIsleContext _context = null!;
    private IBridgeClient _client = null!;
    private VoicePlayerRegistry _registry = null!;
    private IRconGateway _rcon = null!;
    private CheckPlayerConnectedToVoiceHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _client = BridgeTestFactory.CreateDefault();
        _registry = new VoicePlayerRegistry(RedisTestFactory.Create(), NullLogger<VoicePlayerRegistry>.Instance);
        _rcon = Substitute.For<IRconGateway>();
        _handler = new CheckPlayerConnectedToVoiceHandler();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<Isle.Domain.Aggregates.Player> AddPlayerAsync(string steamId, bool isAdmin = false)
    {
        var player = TestData.Player(steamId);
        player.IsAdmin = isAdmin;
        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return player;
    }

    // --- Handle(PlayerConnectedEvent) -------------------------------------------------------------

    [Test]
    public async Task HandlePlayerConnected_AlwaysNotifiesTheClient()
    {
        var @event = new PlayerConnectedEvent { PlayerId = "player_missing", SteamId = "steam_1" };

        await _handler.Handle(@event, _client, _context);

        await _client.Received(1).NotifyAsync("steam_1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandlePlayerConnected_UnknownPlayer_ReturnsNull()
    {
        var @event = new PlayerConnectedEvent { PlayerId = "player_missing", SteamId = "steam_1" };

        var result = await _handler.Handle(@event, _client, _context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task HandlePlayerConnected_AdminPlayer_ReturnsNull()
    {
        var player = await AddPlayerAsync("steam_admin", isAdmin: true);
        var @event = new PlayerConnectedEvent { PlayerId = player.Id, SteamId = player.SteamId };

        var result = await _handler.Handle(@event, _client, _context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task HandlePlayerConnected_RegularPlayer_SchedulesEnsureConnectedCheck()
    {
        var player = await AddPlayerAsync("steam_regular");
        var @event = new PlayerConnectedEvent { PlayerId = player.Id, SteamId = player.SteamId };

        var result = await _handler.Handle(@event, _client, _context);

        Assert.That(result, Is.InstanceOf<DeliveryMessage<EnsurePlayerConnectedToVoiceEvent>>());
        var delivery = (DeliveryMessage<EnsurePlayerConnectedToVoiceEvent>)result!;
        Assert.That(delivery.Message.PlayerId, Is.EqualTo(player.Id));
        Assert.That(delivery.Message.SteamId, Is.EqualTo(player.SteamId));
        Assert.That(delivery.Message.KickAt, Is.EqualTo(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10)).Within(TimeSpan.FromSeconds(5)));
        Assert.That(delivery.Options.ScheduleDelay, Is.EqualTo(TimeSpan.FromSeconds(60)));
    }

    // --- Handle(EnsurePlayerConnectedToVoiceEvent) ------------------------------------------------

    [Test]
    public async Task HandleEnsureConnected_AlreadyRegisteredInVoice_ReturnsNull()
    {
        var player = await AddPlayerAsync("steam_regular");
        await _registry.RegisterAsync(player.Id, player.SteamId);
        var @event = new EnsurePlayerConnectedToVoiceEvent
        {
            PlayerId = player.Id, SteamId = player.SteamId, KickAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        var result = await _handler.Handle(@event, _client, _registry, _rcon, _context);

        Assert.That(result, Is.Null);
        await _rcon.DidNotReceive().ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleEnsureConnected_RegisteredWithBlankPlayerId_FallsThroughToPlayerLookup()
    {
        // Edge case: the registry has an entry for this steamId, but the mapped playerId is blank.
        var player = await AddPlayerAsync("steam_regular");
        await _registry.RegisterAsync("", player.SteamId);
        var @event = new EnsurePlayerConnectedToVoiceEvent
        {
            PlayerId = player.Id, SteamId = player.SteamId, KickAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        var result = await _handler.Handle(@event, _client, _registry, _rcon, _context);

        // Falls through past the registry check into the ordinary reminder path.
        Assert.That(result, Is.InstanceOf<DeliveryMessage<EnsurePlayerConnectedToVoiceEvent>>());
    }

    [Test]
    public async Task HandleEnsureConnected_UnknownPlayer_ReturnsNull()
    {
        var @event = new EnsurePlayerConnectedToVoiceEvent
        {
            PlayerId = "player_missing", SteamId = "steam_missing", KickAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        var result = await _handler.Handle(@event, _client, _registry, _rcon, _context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task HandleEnsureConnected_AdminPlayer_ReturnsNull()
    {
        var player = await AddPlayerAsync("steam_admin", isAdmin: true);
        var @event = new EnsurePlayerConnectedToVoiceEvent
        {
            PlayerId = player.Id, SteamId = player.SteamId, KickAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        var result = await _handler.Handle(@event, _client, _registry, _rcon, _context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task HandleEnsureConnected_GracePeriodExpired_KicksThePlayerAndReturnsNull()
    {
        var player = await AddPlayerAsync("steam_regular");
        var @event = new EnsurePlayerConnectedToVoiceEvent
        {
            PlayerId = player.Id, SteamId = player.SteamId, KickAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        };

        var result = await _handler.Handle(@event, _client, _registry, _rcon, _context);

        Assert.That(result, Is.Null);
        await _rcon.Received(1).ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task>>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().DmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IsleBridge.Sdk.Models.ChatMode>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleEnsureConnected_StillWithinGracePeriod_SendsReminderAndReschedules()
    {
        var player = await AddPlayerAsync("steam_regular");
        var @event = new EnsurePlayerConnectedToVoiceEvent
        {
            PlayerId = player.Id, SteamId = player.SteamId, KickAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        var result = await _handler.Handle(@event, _client, _registry, _rcon, _context);

        await _client.Received(1).DmAsync(Arg.Any<string>(), player.SteamId, "VENTA.GG", Arg.Any<IsleBridge.Sdk.Models.ChatMode>(), Arg.Any<CancellationToken>());
        Assert.That(result, Is.InstanceOf<DeliveryMessage<EnsurePlayerConnectedToVoiceEvent>>());
        var delivery = (DeliveryMessage<EnsurePlayerConnectedToVoiceEvent>)result!;
        // Remaining time (~5 min) exceeds the 120s reminder cadence, so the cadence wins.
        Assert.That(delivery.Options.ScheduleDelay, Is.EqualTo(TimeSpan.FromSeconds(120)));
    }

    [Test]
    public async Task HandleEnsureConnected_RemainingTimeShorterThanCadence_ReschedulesForRemainingTimeOnly()
    {
        var player = await AddPlayerAsync("steam_regular");
        var @event = new EnsurePlayerConnectedToVoiceEvent
        {
            PlayerId = player.Id, SteamId = player.SteamId, KickAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };

        var result = await _handler.Handle(@event, _client, _registry, _rcon, _context);

        var delivery = (DeliveryMessage<EnsurePlayerConnectedToVoiceEvent>)result!;
        // Remaining time (~30s) is shorter than the 120s cadence, so the final tick lands at KickAt.
        Assert.That(delivery.Options.ScheduleDelay, Is.EqualTo(TimeSpan.FromSeconds(30)).Within(TimeSpan.FromSeconds(2)));
    }
}
