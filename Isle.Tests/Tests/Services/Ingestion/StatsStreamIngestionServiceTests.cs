using Isle.Api.Services.Ingestion;
using Isle.Api.Services.State;
using Isle.Contracts.Commands;
using Isle.Tests.Helpers;
using Isle.Tests.Helpers.Redis;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Services.Ingestion;

/// <summary>
/// Covers <see cref="StatsStreamIngestionService"/>: the voice opt-in pre-filter and the
/// touch/translate/re-check logic in PublishAsync, which is the hottest path in the service (about
/// once per second per in-voice player). Both are `protected override`, invoked via
/// <see cref="ProtectedInvoke"/> - see BridgeStreamIngestionServiceTests for the shared read loop.
/// </summary>
[TestFixture]
public class StatsStreamIngestionServiceTests
{
    private VoicePlayerRegistry _registry = null!;
    private StatsStreamIngestionService _service = null!;
    private IMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = Substitute.For<IMessageBus>();
        _registry = new VoicePlayerRegistry(RedisTestFactory.Create(), NullLogger<VoicePlayerRegistry>.Instance);
        _service = new StatsStreamIngestionService(
            Substitute.For<IStatsStream>(),
            _registry,
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<StatsStreamIngestionService>.Instance);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    private bool IsRelevant(StatsSnapshot s) => ProtectedInvoke.Invoke<bool>(_service, "IsRelevant", s);

    private Task Publish(StatsSnapshot s) =>
        ProtectedInvoke.InvokeTaskAsync(_service, "PublishAsync", s, _bus, CancellationToken.None);

    // ── IsRelevant: voice opt-in pre-filter ──────────────────────────────────

    [Test]
    public void IsRelevant_PlayerOptedIntoVoice_IsTrue()
    {
        _registry.RegisterAsync("player_1", "steam_1").GetAwaiter().GetResult();

        Assert.That(IsRelevant(new StatsSnapshot { Steam = "steam_1" }), Is.True);
    }

    [Test]
    public void IsRelevant_PlayerNotOptedIntoVoice_IsFalse()
    {
        Assert.That(IsRelevant(new StatsSnapshot { Steam = "steam_unregistered" }), Is.False);
    }

    // ── PublishAsync: touch + translate + re-check ──────────────────────────

    [Test]
    public async Task PublishAsync_NoPositionInSnapshot_TouchesButPublishesNothing()
    {
        await _registry.RegisterAsync("player_1", "steam_1");

        await Publish(new StatsSnapshot { Steam = "steam_1", Pos = null });

        Assert.That(_bus.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public async Task PublishAsync_WithPosition_PublishesUpdatePlayerPositionCommandMappedToThePlayerId()
    {
        await _registry.RegisterAsync("player_1", "steam_1");

        await Publish(new StatsSnapshot
        {
            Steam = "steam_1",
            Pos = new Position { X = 10, Y = 20, Z = 30 },
            Rot = new Rotation { Yaw = 90 },
        });

        await _bus.Received(1).PublishAsync(Arg.Is<UpdatePlayerPositionCommand>(c =>
            c.PlayerId == "player_1" && c.WorldX == 10f && c.WorldY == 20f && c.WorldZ == 30f && c.Yaw == 90f));
    }

    [Test]
    public async Task PublishAsync_NoRotationInSnapshot_DefaultsYawToZero()
    {
        await _registry.RegisterAsync("player_1", "steam_1");

        await Publish(new StatsSnapshot { Steam = "steam_1", Pos = new Position { X = 1, Y = 2, Z = 3 }, Rot = null });

        await _bus.Received(1).PublishAsync(Arg.Is<UpdatePlayerPositionCommand>(c => c.Yaw == 0f));
    }

    [Test]
    public async Task PublishAsync_PlayerLeftVoiceBetweenTheFilterAndThePublish_ReRunsTheCheckAndPublishesNothing()
    {
        await _registry.RegisterAsync("player_1", "steam_1");
        // Simulate a leave landing on the game-event feed after IsRelevant already let this
        // message through but before PublishAsync ran - the doc comment on PublishAsync calls this
        // out explicitly ("Re-read rather than trusting the pre-filter").
        await _registry.UnregisterAsync("player_1");

        await Publish(new StatsSnapshot { Steam = "steam_1", Pos = new Position { X = 1, Y = 2, Z = 3 } });

        Assert.That(_bus.ReceivedCalls(), Is.Empty);
    }
}
