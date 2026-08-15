using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Voice.Rooms;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Echo.Voice.Testing;

/// <summary>One captured server-to-client push.</summary>
public sealed record CapturedSend(string Target, string Method, object?[] Args)
{
    /// <summary>The envelope <see cref="VoiceAnnouncer"/> builds.</summary>
    public IReadOnlyDictionary<string, object?>? Envelope => Args.ElementAtOrDefault(0) as Dictionary<string, object?>;

    public VoiceRoomSnapshot? Snapshot => Args.ElementAtOrDefault(0) as VoiceRoomSnapshot;

    public long? Version => Envelope?.TryGetValue("version", out var v) == true ? v as long? : Snapshot?.Version;
}

/// <summary>Everything wired together over an in-memory cache, so the tests exercise the real
/// store, announcer, service and reconciler rather than substitutes for them.</summary>
public sealed class VoiceTestHarness
{
    public IDistributedCache Cache { get; }
    public VoiceRoomStore Rooms { get; }
    public VoiceAnnouncer Announcer { get; }
    public VoiceRoomService Service { get; }
    public VoiceReconciler Reconciler { get; }
    public List<CapturedSend> Sends { get; }

    /// <summary>
    /// One service instance.
    /// </summary>
    /// <param name="locks">Shared across instances in production - it is a Redis lock, and it is
    /// the only thing serialising two pods writing the same room.</param>
    /// <param name="cache">Pass the same instance to two harnesses to model two pods backed by one
    /// Redis, which is the real deployment: Guild and Messaging each run two or more replicas and a
    /// room's participants are spread across them arbitrarily.</param>
    /// <param name="sends">Pass the same list to two harnesses to model the SignalR backplane -
    /// clients are connected to the gateway, not to these instances, so a push from either pod
    /// reaches any participant.</param>
    public VoiceTestHarness(
        IDistributedLockService? locks = null,
        IDistributedCache? cache = null,
        List<CapturedSend>? sends = null)
    {
        // Interleaving by default: a cache that completes synchronously turns every concurrency
        // test into a no-op, because nothing ever gets a chance to race.
        Cache = cache ?? new InterleavingDistributedCache(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        Sends = sends ?? [];
        locks ??= new SerializingLockService();
        Rooms = new VoiceRoomStore(
            new LockedJsonCacheStore(locks, Cache), locks, Cache, NullLogger<VoiceRoomStore>.Instance);
        Announcer = new VoiceAnnouncer(new CapturingHubContext(Sends));
        Service = new VoiceRoomService(Rooms, Announcer);
        Reconciler = new VoiceReconciler(Rooms, Announcer, Cache, NullLogger<VoiceReconciler>.Instance);
    }

    /// <summary>A <see cref="VoiceRoomStore"/> over a caller-supplied cache and lock.</summary>
    public static VoiceRoomStore StoreFor(IDistributedCache cache, IDistributedLockService locks) =>
        new(new LockedJsonCacheStore(locks, cache), locks, cache, NullLogger<VoiceRoomStore>.Instance);

    /// <summary>A <see cref="VoiceRoomService"/> over a caller-supplied cache, lock and hub - for
    /// service suites wiring a controller or handler under test to their own fakes.</summary>
    /// <param name="entitlements">Null resolves every key to its catalogue default, which is
    /// unlimited - the shape a suite that is not about limits wants. A suite that <em>is</em> about
    /// them passes a resolver here rather than reaching past the controller, because the whole point
    /// of a controller-level entitlement test is that the endpoint consults one at all.</param>
    /// <param name="ceilings">What this box will carry, as opposed to what a guild has paid for.</param>
    /// <param name="subscriptions">Null keeps the all-to-all behaviour, which is what every suite
    /// that is not about subscription planning wants.</param>
    public static VoiceRoomService ServiceFor(
        IDistributedCache cache,
        IDistributedLockService locks,
        IHubContext<EchoRealtimeHub> hub,
        EntitlementResolver? entitlements = null,
        OperatorCeilings? ceilings = null,
        VoiceSubscriptions? subscriptions = null) =>
        new(StoreFor(cache, locks), new VoiceAnnouncer(hub), subscriptions, entitlements, ceilings);

    /// <summary>A <see cref="VoiceReconciler"/> over caller-supplied fakes.</summary>
    public static VoiceReconciler ReconcilerFor(
        IDistributedCache cache, IDistributedLockService locks, IHubContext<EchoRealtimeHub> hub) =>
        new(StoreFor(cache, locks), new VoiceAnnouncer(hub), cache, NullLogger<VoiceReconciler>.Instance);

    /// <summary>Writes a room straight into <paramref name="cache"/> at the shared key, for suites
    /// that seed state rather than build it through the service.</summary>
    public static Task SeedRoomAsync(IDistributedCache cache, VoiceRoom room, CancellationToken ct = default) =>
        cache.SetStringAsync(room.Key.CacheKey, System.Text.Json.JsonSerializer.Serialize(room), ct);

    /// <summary>Reads a room back out of <paramref name="cache"/> at the shared key.</summary>
    public static async Task<VoiceRoom?> ReadRoomAsync(
        IDistributedCache cache, VoiceRoomKey key, CancellationToken ct = default)
    {
        var raw = await cache.GetStringAsync(key.CacheKey, ct);
        return raw is null ? null : System.Text.Json.JsonSerializer.Deserialize<VoiceRoom>(raw);
    }

    public List<CapturedSend> SendsOf(string method) =>
        Sends.Where(s => s.Method == method).ToList();

    public List<CapturedSend> SendsTo(string userId) =>
        Sends.Where(s => s.Target == $"user:{userId}").ToList();

    public void ClearSends() => Sends.Clear();

    /// <summary>
    /// Forces the read-modify-write window open by yielding inside every cache operation.
    /// </summary>
    public sealed class InterleavingDistributedCache(IDistributedCache inner) : IDistributedCache
    {
        public byte[]? Get(string key) => inner.Get(key);

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            await Task.Yield();
            return await inner.GetAsync(key, token);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            inner.Set(key, value, options);

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            await Task.Yield();
            await inner.SetAsync(key, value, options, token);
        }

        public void Refresh(string key) => inner.Refresh(key);

        public async Task RefreshAsync(string key, CancellationToken token = default)
        {
            await Task.Yield();
            await inner.RefreshAsync(key, token);
        }

        public void Remove(string key) => inner.Remove(key);

        public async Task RemoveAsync(string key, CancellationToken token = default)
        {
            await Task.Yield();
            await inner.RemoveAsync(key, token);
        }
    }

    /// <summary>
    /// A real per-key mutex, so concurrency tests exercise the same serialisation the Redis lock
    /// provides rather than a no-op that would make every lost-update test pass vacuously.
    /// </summary>
    public sealed class SerializingLockService : IDistributedLockService
    {
        private readonly Dictionary<string, SemaphoreSlim> _gates = new();

        private SemaphoreSlim Gate(string key)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(key, out var gate))
                    _gates[key] = gate = new SemaphoreSlim(1, 1);
                return gate;
            }
        }

        public async Task<IAsyncDisposable> AcquireAsync(
            string key, TimeSpan? wait = null, CancellationToken ct = default)
        {
            var gate = Gate(key);
            if (!await gate.WaitAsync(wait ?? TimeSpan.FromSeconds(5), ct))
                throw new TimeoutException($"lock '{key}' unavailable");
            return new Handle(gate);
        }

        private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Times out the first <c>failures</c> acquisitions, then behaves normally - the contended-room
    /// case, which in production is a busy channel and in the old code was an unhandled
    /// <see cref="TimeoutException"/> surfacing as a 500 mid-call.
    /// </summary>
    public sealed class FlakyLockService(int failures) : IDistributedLockService
    {
        public int Attempts { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(
            string key, TimeSpan? wait = null, CancellationToken ct = default)
        {
            Attempts++;
            if (Attempts <= failures) throw new TimeoutException($"lock '{key}' unavailable");
            return Task.FromResult<IAsyncDisposable>(new Handle());
        }

        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Always times out - a room that never frees up.</summary>
    public sealed class DeadlockedLockService : IDistributedLockService
    {
        public int Attempts { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(
            string key, TimeSpan? wait = null, CancellationToken ct = default)
        {
            Attempts++;
            throw new TimeoutException($"lock '{key}' unavailable");
        }
    }

    private sealed class CapturingHubContext(List<CapturedSend> sends) : IHubContext<EchoRealtimeHub>
    {
        public IHubClients Clients { get; } = new CapturingClients(sends);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class CapturingClients(List<CapturedSend> sends) : IHubClients
    {
        public IClientProxy All => new CapturingProxy("all", sends);
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => new CapturingProxy("allExcept", sends);
        public IClientProxy Client(string connectionId) => new CapturingProxy($"connection:{connectionId}", sends);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new CapturingProxy("connections", sends);
        public IClientProxy Group(string groupName) => new CapturingProxy($"group:{groupName}", sends);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new CapturingProxy("groups", sends);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => new CapturingProxy("groupExcept", sends);
        public IClientProxy User(string userId) => new CapturingProxy($"user:{userId}", sends);

        /// <summary>Fans out to one capture per recipient, so a test can assert who was told what
        /// without knowing whether the announcer used a single or a multi-user send.</summary>
        public IClientProxy Users(IReadOnlyList<string> userIds) => new MultiProxy(userIds, sends);
    }

    private sealed class CapturingProxy(string target, List<CapturedSend> sends) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            sends.Add(new CapturedSend(target, method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class MultiProxy(IReadOnlyList<string> userIds, List<CapturedSend> sends) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            foreach (var userId in userIds)
                sends.Add(new CapturedSend($"user:{userId}", method, args));
            return Task.CompletedTask;
        }
    }
}
