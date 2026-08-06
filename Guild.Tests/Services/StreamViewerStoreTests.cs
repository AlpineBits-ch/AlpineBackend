using Echo.Realtime.Caching;
using Guild.Tests.Helpers;

namespace Guild.Tests.Services;

/// <summary>
/// Covers <see cref="StreamViewerStore"/>, which answers "who is watching this screen share" for
/// both guild voice channels and direct calls.
/// </summary>
[TestFixture]
public class StreamViewerStoreTests
{
    private const string Scope = "channel:channel-1";

    private FakeDistributedCache _cache = null!;
    private TestClock _clock = null!;
    private StreamViewerStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        _store = new StreamViewerStore(new FakeDistributedLockService(), _cache) { Clock = _clock };
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Watch_RecordsTheViewer()
    {
        var snapshot = await _store.WatchAsync(Scope, "share-1", "user-1");

        Assert.That(snapshot["share-1"], Is.EqualTo(new[] { "user-1" }));
    }

    [Test]
    public async Task Watch_FirstViewerOfTheFirstShare_CreatesTheTable()
    {
        // The store's mutate path is a load-or-create, not LockedJsonCacheStore.UpdateAsync, which
        // no-ops when the key is absent.
        Assert.That(_cache.HasEntry("stream:viewers:" + Scope), Is.False);

        await _store.WatchAsync(Scope, "share-1", "user-1");

        Assert.That(_cache.HasEntry("stream:viewers:" + Scope), Is.True);
    }

    [Test]
    public async Task Watch_IsIdempotentForTheSameViewer()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");
        var snapshot = await _store.WatchAsync(Scope, "share-1", "user-1");

        Assert.That(snapshot["share-1"], Has.Count.EqualTo(1),
            "re-announcing is the heartbeat, not a second viewer");
    }

    [Test]
    public async Task Watch_RefreshesAnExistingClaimRatherThanLettingItExpire()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");

        // Just under the TTL, the client re-announces - the expiry must move with it, or a viewer
        // who never stopped watching would drop out on the next read.
        _clock.Advance(StreamViewerStore.ViewerTtl - TimeSpan.FromSeconds(5));
        await _store.WatchAsync(Scope, "share-1", "user-1");
        _clock.Advance(TimeSpan.FromSeconds(10));

        var snapshot = await _store.SnapshotAsync(Scope);
        Assert.That(snapshot["share-1"], Is.EqualTo(new[] { "user-1" }));
    }

    [Test]
    public async Task Snapshot_DropsAViewerWhoStoppedAnnouncing()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");

        _clock.Advance(StreamViewerStore.ViewerTtl + TimeSpan.FromSeconds(1));

        Assert.That(await _store.SnapshotAsync(Scope), Is.Empty,
            "a client that went away sends no unwatch - expiry is the only thing that can remove it");
    }

    [Test]
    public async Task Snapshot_KeepsViewersWhoAreStillWithinTheirWindow()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");
        _clock.Advance(StreamViewerStore.ViewerTtl - TimeSpan.FromSeconds(1));

        var snapshot = await _store.SnapshotAsync(Scope);

        Assert.That(snapshot["share-1"], Is.EqualTo(new[] { "user-1" }));
    }

    [Test]
    public async Task Unwatch_RemovesOnlyThatViewer()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");
        await _store.WatchAsync(Scope, "share-1", "user-2");

        var snapshot = await _store.UnwatchAsync(Scope, "share-1", "user-1");

        Assert.That(snapshot["share-1"], Is.EqualTo(new[] { "user-2" }));
    }

    [Test]
    public async Task Unwatch_OfTheLastViewer_DropsTheShareEntirely()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");

        var snapshot = await _store.UnwatchAsync(Scope, "share-1", "user-1");

        Assert.That(snapshot, Is.Empty,
            "an empty audience is reported as no entry, so callers never have to distinguish the two");
    }

    [Test]
    public async Task RemoveViewer_DropsEveryClaimThatUserHolds()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");
        await _store.WatchAsync(Scope, "share-2", "user-1");
        await _store.WatchAsync(Scope, "share-1", "user-2");

        var snapshot = await _store.RemoveViewerAsync(Scope, "user-1");

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ContainsKey("share-2"), Is.False);
            Assert.That(snapshot["share-1"], Is.EqualTo(new[] { "user-2" }));
        });
    }

    [Test]
    public async Task RemoveShare_ForgetsTheAudienceOfAStreamThatStopped()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");
        await _store.WatchAsync(Scope, "share-2", "user-1");

        var snapshot = await _store.RemoveShareAsync(Scope, "share-1");

        Assert.That(snapshot.Keys, Is.EqualTo(new[] { "share-2" }));
    }

    [Test]
    public async Task RemoveShare_MeansAReusedShareIdStartsEmpty()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");
        await _store.RemoveShareAsync(Scope, "share-1");

        var snapshot = await _store.SnapshotAsync(Scope);

        Assert.That(snapshot.ContainsKey("share-1"), Is.False,
            "inheriting the previous stream's viewers would show an audience that never joined");
    }

    [Test]
    public async Task RemoveShares_ForgetsSeveralAtOnce()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");
        await _store.WatchAsync(Scope, "share-2", "user-1");
        await _store.WatchAsync(Scope, "share-3", "user-1");

        var snapshot = await _store.RemoveSharesAsync(Scope, ["share-1", "share-3"]);

        Assert.That(snapshot.Keys, Is.EqualTo(new[] { "share-2" }));
    }

    [Test]
    public async Task Drop_ClearsTheWholeScope()
    {
        await _store.WatchAsync(Scope, "share-1", "user-1");

        await _store.DropAsync(Scope);

        Assert.That(await _store.SnapshotAsync(Scope), Is.Empty);
    }

    [Test]
    public async Task Scopes_AreIndependent()
    {
        var other = StreamViewerStore.CallScope("call-1");
        await _store.WatchAsync(Scope, "share-1", "user-1");

        await _store.DropAsync(other);

        Assert.That((await _store.SnapshotAsync(Scope))["share-1"], Is.EqualTo(new[] { "user-1" }),
            "a guild channel and a call must not be able to clear each other's viewers");
    }

    [Test]
    public void ScopeNames_AreDistinctPerSurface()
    {
        Assert.That(StreamViewerStore.ChannelScope("x"), Is.Not.EqualTo(StreamViewerStore.CallScope("x")),
            "a channel id and a call id are drawn from different id spaces and could collide");
    }
}
