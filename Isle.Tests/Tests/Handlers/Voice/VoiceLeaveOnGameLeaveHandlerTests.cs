using Isle.Api.Handlers.Voice;
using Isle.Api.Services.State;
using Isle.Contracts.Commands;
using Isle.Contracts.Events.Player;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Handlers.Voice;

[TestFixture]
public class VoiceLeaveOnGameLeaveHandlerTests
{
    private VoicePlayerRegistry _registry = null!;

    [SetUp]
    public void SetUp() =>
        _registry = new VoicePlayerRegistry(RedisTestFactory.Create(), NullLogger<VoicePlayerRegistry>.Instance);

    [Test]
    public async Task Handle_NotOptedIntoVoice_ReturnsNull()
    {
        var result = await VoiceLeaveOnGameLeaveHandler.Handle(new UserLeftIsleServerEvent { SteamId = "steam_ghost" }, _registry);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Handle_RegisteredPlayer_UnregistersAndReturnsRemovePlayerCommand()
    {
        await _registry.RegisterAsync("player_1", "steam_1");

        var result = await VoiceLeaveOnGameLeaveHandler.Handle(new UserLeftIsleServerEvent { SteamId = "steam_1" }, _registry);

        Assert.That(result, Is.InstanceOf<RemovePlayerCommand>());
        Assert.That(((RemovePlayerCommand)result!).PlayerId, Is.EqualTo("player_1"));
        Assert.That(_registry.TryGetSteamId("player_1", out _), Is.False, "the registry entry must be cleared");
        Assert.That(_registry.TryGetPlayerId("steam_1", out _), Is.False);
    }
}
