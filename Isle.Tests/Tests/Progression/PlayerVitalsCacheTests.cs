using System.Text.Json;
using Isle.Api.Services.State;
using Isle.Tests.Helpers;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Isle.Tests.Tests.Progression;

/// <summary>The live-vitals cache.</summary>
[TestFixture]
public class PlayerVitalsCacheTests
{
    private FakeDistributedCache _cache = null!;
    private PlayerVitalsCache _vitals = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _vitals = new PlayerVitalsCache(_cache, NullLogger<PlayerVitalsCache>.Instance);
    }

    private static StatsSnapshot Snapshot(
        string steam = "steam-1",
        string? species = IsleBridge.Sdk.Species.Tyrannosaurus,
        double hp = 50,
        double hpMax = 100,
        Position? pos = null) => new()
    {
        Steam = steam,
        Species = species,
        Growth = 0.75,
        Pos = pos ?? new Position { X = 1000, Y = 2000, Z = 30 },
        Vitals = new Vitals
        {
            Hp = hp,
            HpMax = hpMax,
            Hunger = 30,
            HungerMax = 60,
            Thirst = 10,
            ThirstMax = 100,
            Stamina = 5,
            StaminaMax = 10,
        },
    };

    // ── Normal ────────────────────────────────────────────────────────────

    [Test]
    public async Task ASnapshotComesBackAsFractionsOfItsOwnMaxima()
    {
        // The engine's scales are not 0..1 across every channel, so dividing at capture is the only
        // point that has to know them.
        await _vitals.CaptureAsync(Snapshot());

        var stored = await _vitals.GetAsync("steam-1");

        Assert.That(stored, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Health, Is.EqualTo(0.5));
            Assert.That(stored.Hunger, Is.EqualTo(0.5));
            Assert.That(stored.Thirst, Is.EqualTo(0.1));
            Assert.That(stored.Stamina, Is.EqualTo(0.5));
            Assert.That(stored.Growth, Is.EqualTo(0.75));
        });
    }

    // ── The TTL ───────────────────────────────────────────────────────────

    [Test]
    public async Task AnEntryIsWrittenWithAnExpiry()
    {
        // The TTL is the correctness property, not a cleanup detail: past it the honest answer is "no
        // live dinosaur", and an expiring key gives that answer without anything remembering to delete.
        DistributedCacheEntryOptionsSpy? seen = null;
        var spy = new RecordingCache(_cache, options => seen = options);

        var vitals = new PlayerVitalsCache(spy, NullLogger<PlayerVitalsCache>.Instance);
        await vitals.CaptureAsync(Snapshot());

        Assert.That(seen, Is.Not.Null);
        Assert.That(seen!.AbsoluteExpirationRelativeToNow, Is.EqualTo(PlayerVitalsCache.Ttl));
    }

    [Test]
    public async Task AnExpiredEntryReadsAsNoLiveDinosaur()
    {
        // The fake cache has no clock, so expiry is modelled the way Redis makes it visible: the key
        // is simply gone.
        await _vitals.CaptureAsync(Snapshot());
        Assert.That(await _vitals.GetAsync("steam-1"), Is.Not.Null);

        _cache.Remove(PlayerVitalsCache.KeyFor("steam-1"));

        Assert.That(await _vitals.GetAsync("steam-1"), Is.Null);
    }

    [Test]
    public async Task WritesAreThrottledPerPlayer()
    {
        // The feed ticks about once a second per player.
        await _vitals.CaptureAsync(Snapshot(hp: 100));
        await _vitals.CaptureAsync(Snapshot(hp: 1));

        var stored = await _vitals.GetAsync("steam-1");

        Assert.That(stored!.Health, Is.EqualTo(1.0), "the second write inside the throttle window is skipped");
    }

    [Test]
    public async Task ThrottlingIsPerPlayerRatherThanGlobal()
    {
        await _vitals.CaptureAsync(Snapshot("steam-1", hp: 100));
        await _vitals.CaptureAsync(Snapshot("steam-2", hp: 100));

        Assert.That(await _vitals.GetAsync("steam-1"), Is.Not.Null);
        Assert.That(await _vitals.GetAsync("steam-2"), Is.Not.Null);
    }

    // ── Negative ──────────────────────────────────────────────────────────

    [Test]
    public async Task APlayerWithNoSnapshotIsNullRatherThanAnEmptyDinosaur()
    {
        // Offline, dead and not respawned, or the feed is down. All ordinary; none is an error.
        Assert.That(await _vitals.GetAsync("steam-nobody"), Is.Null);
        Assert.That(await _vitals.GetAsync(""), Is.Null);
    }

    [Test]
    public async Task ASnapshotWithNoPositionIsNotCached()
    {
        // No position means no live pawn - there is nothing to show.
        await _vitals.CaptureAsync(new StatsSnapshot { Steam = "steam-1", Pos = null });

        Assert.That(await _vitals.GetAsync("steam-1"), Is.Null);
    }

    [Test]
    public async Task AChannelWithNoMaximumIsNullRatherThanZero()
    {
        // A hardcoded "full" would be a guess; reporting nothing lets the display say so.
        await _vitals.CaptureAsync(Snapshot(hpMax: 0));

        Assert.That((await _vitals.GetAsync("steam-1"))!.Health, Is.Null);
    }

    [Test]
    public async Task ARedisFailureNeverEscapesIntoTheIngestionLoop()
    {
        // This runs inside the stats read loop; throwing here would drop the SSE connection and take
        // proximity voice down with it, for the least important data on that path.
        _cache.Broken = true;

        Assert.DoesNotThrowAsync(async () => await _vitals.CaptureAsync(Snapshot()));
        Assert.DoesNotThrowAsync(async () => await _vitals.GetAsync("steam-1"));
    }

    [Test]
    public async Task AFailedWriteIsRetriedRatherThanCostingAWholeThrottleWindow()
    {
        _cache.Broken = true;
        await _vitals.CaptureAsync(Snapshot(hp: 100));

        _cache.Broken = false;
        await _vitals.CaptureAsync(Snapshot(hp: 100));

        Assert.That(await _vitals.GetAsync("steam-1"), Is.Not.Null);
    }

    [Test]
    public async Task AnUnreadableStoredValueReadsAsNoLiveDinosaur()
    {
        // A value written by an older shape. Same answer as absent, which callers already handle.
        _cache.SetEntry(PlayerVitalsCache.KeyFor("steam-1"), "{ this is not json");

        Assert.That(await _vitals.GetAsync("steam-1"), Is.Null);
    }

    [Test]
    public async Task ThePositionIsStoredButIsNotWhatCallersAreGiven()
    {
        // Pinning the boundary, not the storage: the cache holds coordinates so the endpoint can turn
        // them into a place name, and the endpoint is the only thing allowed to read them.
        await _vitals.CaptureAsync(Snapshot(pos: new Position { X = 12345, Y = 67890, Z = 5 }));

        var raw = _cache.ReadEntry(PlayerVitalsCache.KeyFor("steam-1"))!;
        var stored = JsonSerializer.Deserialize<PlayerVitals>(raw)!;

        Assert.That(stored.X, Is.EqualTo(12345));
        Assert.That(stored.Y, Is.EqualTo(67890));
    }
}

/// <summary>The cache options one write was made with.</summary>
internal sealed record DistributedCacheEntryOptionsSpy(TimeSpan? AbsoluteExpirationRelativeToNow);

/// <summary>Wraps <see cref="FakeDistributedCache"/> and reports the options each write used, which is
/// the only way to see a TTL that an in-process dictionary does not otherwise honour.</summary>
internal sealed class RecordingCache(
    FakeDistributedCache inner,
    Action<DistributedCacheEntryOptionsSpy> onWrite)
    : Microsoft.Extensions.Caching.Distributed.IDistributedCache
{
    public byte[]? Get(string key) => inner.Get(key);

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => inner.GetAsync(key, token);

    public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options)
    {
        onWrite(new DistributedCacheEntryOptionsSpy(options.AbsoluteExpirationRelativeToNow));
        inner.Set(key, value, options);
    }

    public Task SetAsync(string key, byte[] value,
        Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }

    public void Refresh(string key) { }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => inner.Remove(key);

    public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
}
