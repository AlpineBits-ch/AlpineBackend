using Echo.Realtime.Caching;

namespace Guild.Tests.Helpers;

/// <summary>No-op IDistributedLockService for unit tests - grants every lock immediately and
/// releases it as a no-op on dispose, since these tests run single-threaded with no real
/// contention to guard against.</summary>
internal sealed class FakeDistributedLockService : IDistributedLockService
{
    public Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan? wait = null, CancellationToken ct = default) =>
        Task.FromResult<IAsyncDisposable>(NoopLock.Instance);

    private sealed class NoopLock : IAsyncDisposable
    {
        public static readonly NoopLock Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
