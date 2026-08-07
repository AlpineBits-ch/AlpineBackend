using Echo.Realtime.Caching;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Microsoft.Extensions.Caching.Distributed;

namespace Echo.Voice.Tests.Rooms;

/// <summary>
/// Behaviour under concurrent writers, which is the normal case: Guild and Messaging each run two
/// or more instances, so every room is reachable from several processes at once and the lock is
/// load-bearing rather than defensive.
/// </summary>
[TestFixture(VoiceRoomKind.Channel, TestName = "Concurrency (guild channel)")]
[TestFixture(VoiceRoomKind.Call, TestName = "Concurrency (direct call)")]
public class VoiceConcurrencyTests(string kind)
{
    private VoiceRoomKey _key;

    [SetUp]
    public void SetUp() => _key = new VoiceRoomKey(kind, "room-1");

    // ── No lost updates ───────────────────────────────────────────────────────

    /// <summary>
    /// A join storm: everyone piles into the same room at once from every instance.
    /// </summary>
    [Test]
    public async Task Thirty_simultaneous_joins_all_land()
    {
        var h = new VoiceTestHarness();
        var users = Enumerable.Range(0, 30).Select(i => $"user-{i:00}").ToList();

        await Task.WhenAll(users.Select(u => h.Service.JoinAsync(_key, u, $"device-{u}")));

        var room = (await h.Rooms.LoadAsync(_key))!;
        Assert.Multiple(() =>
        {
            Assert.That(room.Participants.Select(p => p.UserId), Is.EquivalentTo(users));
            Assert.That(room.Version, Is.EqualTo(30),
                "one bump per applied mutation - a lower count means a write was lost, a higher one "
                + "means a mutation was applied twice");
        });
    }

    /// <summary>Guards the guard.</summary>
    [Test]
    public async Task Without_the_lock_a_join_storm_loses_almost_everything()
    {
        var h = new VoiceTestHarness(new UnsafeNoLockService());
        var users = Enumerable.Range(0, 30).Select(i => $"user-{i:00}").ToList();

        await Task.WhenAll(users.Select(u => h.Service.JoinAsync(_key, u, $"device-{u}")));

        var room = (await h.Rooms.LoadAsync(_key))!;
        Assert.That(room.Participants, Has.Count.LessThan(users.Count),
            "if this passes, the locked tests above are proving nothing - check that the cache "
            + "still yields between read and write");
    }

    /// <summary>Deliberately grants without excluding anyone. Only for the test above.</summary>
    private sealed class UnsafeNoLockService : IDistributedLockService
    {
        public Task<IAsyncDisposable> AcquireAsync(
            string key, TimeSpan? wait = null, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable>(new Handle());

        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task Concurrent_publishes_are_all_recorded()
    {
        var h = new VoiceTestHarness();
        var users = Enumerable.Range(0, 12).Select(i => $"user-{i:00}").ToList();
        foreach (var u in users) await h.Service.JoinAsync(_key, u, $"device-{u}");

        await Task.WhenAll(users.Select(u => h.Service.RecordPublishAsync(_key, u, $"cf-{u}")));

        var room = (await h.Rooms.LoadAsync(_key))!;
        Assert.That(room.Participants.All(p => p.PublishState == VoicePublishState.Publishing), Is.True,
            "every publisher must be pullable; one silently dropped is one participant nobody can hear");
    }

    [Test]
    public async Task Concurrent_mixed_writes_leave_a_coherent_room()
    {
        var h = new VoiceTestHarness();
        foreach (var u in new[] { "a", "b", "c" }) await h.Service.JoinAsync(_key, u, $"device-{u}");

        await Task.WhenAll(
            h.Service.RecordPublishAsync(_key, "a", "cf-a"),
            h.Service.SetMuteAsync(_key, "b", true, serverForced: false),
            h.Service.SetDeafenAsync(_key, "c", true, serverForced: false),
            h.Service.RecordTracksAsync(_key, "a", "cf-a", ["screen-s1"]),
            h.Service.JoinAsync(_key, "d", "device-d"));

        var room = (await h.Rooms.LoadAsync(_key))!;
        Assert.Multiple(() =>
        {
            Assert.That(room.Participants, Has.Count.EqualTo(4));
            Assert.That(room.Find("b")!.IsSelfMuted, Is.True);
            Assert.That(room.Find("c")!.IsSelfDeafened, Is.True);
            Assert.That(room.Find("a")!.MediaSessionId, Is.EqualTo("cf-a"));
            Assert.That(room.Find("a")!.ActiveScreenShares.Single().ShareId, Is.EqualTo("s1"));
        });
    }

    /// <summary>The version is what clients use to order and detect gaps, so it must never repeat
    /// or go backwards regardless of how writes interleave.</summary>
    [Test]
    public async Task Versions_are_unique_and_monotonic_under_concurrency()
    {
        var h = new VoiceTestHarness();
        var users = Enumerable.Range(0, 20).Select(i => $"user-{i:00}").ToList();

        var rooms = await Task.WhenAll(users.Select(u => h.Service.JoinAsync(_key, u, $"device-{u}")));
        var versions = rooms.Select(r => r.Version).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(versions.Distinct().Count(), Is.EqualTo(versions.Count), "a repeated version");
            Assert.That(versions.Order(), Is.EqualTo(Enumerable.Range(1, users.Count).Select(i => (long)i)));
        });
    }

    // ── Contention is retried, not thrown at the user ─────────────────────────

    /// <summary>
    /// A contended room used to surface as a bare <see cref="TimeoutException"/>, which reached the
    /// client as an opaque 500 in the middle of a live call.
    /// </summary>
    [Test]
    public async Task A_transiently_contended_room_is_retried_and_the_change_still_lands()
    {
        var flaky = new VoiceTestHarness.FlakyLockService(failures: 3);
        var h = new VoiceTestHarness(flaky);

        var room = await h.Service.JoinAsync(_key, "alice", "device-1");

        Assert.Multiple(() =>
        {
            Assert.That(room.Find("alice"), Is.Not.Null);
            Assert.That(flaky.Attempts, Is.EqualTo(4), "three failures then the successful attempt");
        });
    }

    /// <summary>
    /// Retries re-run the mutation from a fresh read, so a mutation expressed as a blind delta
    /// would double-apply.
    /// </summary>
    [Test]
    public async Task A_retried_mutation_applies_exactly_once()
    {
        var flaky = new VoiceTestHarness.FlakyLockService(failures: 4);
        var h = new VoiceTestHarness(flaky);

        var room = await h.Service.JoinAsync(_key, "alice", "device-1");

        Assert.Multiple(() =>
        {
            Assert.That(room.Participants.Count(p => p.UserId == "alice"), Is.EqualTo(1));
            Assert.That(room.Version, Is.EqualTo(1), "the retries must not each bump the version");
        });
    }

    [Test]
    public async Task A_room_that_never_frees_up_fails_with_a_typed_error()
    {
        var dead = new VoiceTestHarness.DeadlockedLockService();
        var h = new VoiceTestHarness(dead);

        var ex = Assert.ThrowsAsync<VoiceRoomContentionException>(
            () => h.Service.JoinAsync(_key, "alice", "device-1"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Key.Id, Is.EqualTo("room-1"));
            Assert.That(ex.Attempts, Is.EqualTo(VoiceRoomStore.RetryDelays.Count + 1));
            Assert.That(dead.Attempts, Is.EqualTo(VoiceRoomStore.RetryDelays.Count + 1),
                "every attempt in the budget is actually used");
            Assert.That(ex, Is.Not.InstanceOf<TimeoutException>(),
                "callers must be able to tell contention from a server fault, and map it to a 503");
        });
    }

    [Test]
    public async Task Reads_do_not_contend()
    {
        var dead = new VoiceTestHarness.DeadlockedLockService();
        var h = new VoiceTestHarness(dead);

        // A snapshot read must stay available even when the room is too hot to write - it is the
        // recovery path, and it is needed most exactly when the room is busy.
        Assert.That(await h.Rooms.LoadAsync(_key), Is.Null);
        Assert.That(dead.Attempts, Is.Zero);
        await Task.CompletedTask;
    }

}
