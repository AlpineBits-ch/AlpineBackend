using Isle.Api.Services.KingOfTheHill;
using Isle.Tests.Helpers.Redis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.KingOfTheHill;

[TestFixture]
public class KingOfTheHillMatchStateStoreTests
{
    private KingOfTheHillMatchStateStore _store = null!;
    private FakeRedisStore _redisStore = null!;

    [SetUp]
    public void SetUp() =>
        _store = new KingOfTheHillMatchStateStore(RedisTestFactory.Create(out _redisStore), NullLogger<KingOfTheHillMatchStateStore>.Instance);

    [Test]
    public async Task ReadAsync_NoMarkerWritten_ReturnsNull() =>
        Assert.That(await _store.ReadAsync(), Is.Null);

    [Test]
    public async Task WriteThenRead_RoundTripsEveryField()
    {
        var state = new KothMatchState("def_1", "game_instance_1", DateTime.UtcNow, ["p1", "p2"]);

        await _store.WriteAsync(state);
        var read = await _store.ReadAsync();

        Assert.That(read, Is.Not.Null);
        Assert.That(read!.DefinitionId, Is.EqualTo(state.DefinitionId));
        Assert.That(read.InstanceId, Is.EqualTo(state.InstanceId));
        Assert.That(read.ParticipantIds, Is.EqualTo(state.ParticipantIds));
        Assert.That(read.StartedAt, Is.EqualTo(state.StartedAt).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public async Task ClearAsync_RemovesTheMarker()
    {
        await _store.WriteAsync(new KothMatchState("def_1", "game_instance_1", DateTime.UtcNow, []));

        await _store.ClearAsync();

        Assert.That(await _store.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task WriteAsync_OverwritesAPreviousMarker()
    {
        await _store.WriteAsync(new KothMatchState("def_1", "game_instance_1", DateTime.UtcNow, []));
        await _store.WriteAsync(new KothMatchState("def_2", "game_instance_2", DateTime.UtcNow, ["p1"]));

        var read = await _store.ReadAsync();

        Assert.That(read!.InstanceId, Is.EqualTo("game_instance_2"));
    }

    [Test]
    public async Task ReadAsync_CorruptedStoredValue_ReturnsNullInsteadOfThrowing()
    {
        _redisStore.Strings["isle:koth:active"] = "{ this is not valid json";

        Assert.That(await _store.ReadAsync(), Is.Null);
    }
}
