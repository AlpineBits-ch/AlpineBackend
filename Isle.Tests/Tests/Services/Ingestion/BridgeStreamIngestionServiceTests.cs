using Isle.Api.Services.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Services.Ingestion;

/// <summary>
/// Covers the shared read-loop behaviour in <see cref="BridgeStreamIngestionService{TMessage}"/> -
/// the IsRelevant pre-filter, one DI scope per accepted message, and that both a mid-stream
/// exception and an outright cancellation let the hosted service stop cleanly without hanging or
/// requiring a real multi-second reconnect delay to elapse.
/// </summary>
[TestFixture]
public class BridgeStreamIngestionServiceTests
{
    private sealed record FakeMessage(string Value);

    /// <summary>Yields the given items, then blocks (without throwing) until <paramref name="ct"/>
    /// is cancelled - keeps the read loop from ever seeing a "clean end of stream", which would
    /// otherwise trigger the base class's real 2s reconnect delay.</summary>
    private static async IAsyncEnumerable<FakeMessage> ScriptedStream(IReadOnlyList<FakeMessage> items, CancellationToken ct)
    {
        foreach (var item in items)
            yield return item;

        var tcs = new TaskCompletionSource();
        await using var _ = ct.Register(() => tcs.TrySetResult());
        await tcs.Task;
    }

    private static async IAsyncEnumerable<FakeMessage> ThrowingStream(CancellationToken ct)
    {
        await Task.Yield();
        throw new InvalidOperationException("bridge feed dropped");
#pragma warning disable CS0162 // unreachable: required so the compiler treats this as an iterator
        yield break;
#pragma warning restore CS0162
    }

    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _count;
        public int CreateScopeCallCount => _count;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _count);
            return inner.CreateScope();
        }
    }

    private sealed class RecordingIngestionService : BridgeStreamIngestionService<FakeMessage>
    {
        private readonly Func<CancellationToken, IAsyncEnumerable<FakeMessage>> _openStream;
        private readonly Func<FakeMessage, bool> _isRelevant;

        public List<FakeMessage> Published { get; } = new();

        public RecordingIngestionService(
            Func<CancellationToken, IAsyncEnumerable<FakeMessage>> openStream,
            IServiceScopeFactory scopeFactory,
            Func<FakeMessage, bool>? isRelevant = null)
            : base(scopeFactory, NullLogger.Instance)
        {
            _openStream = openStream;
            _isRelevant = isRelevant ?? (_ => true);
        }

        protected override string StreamName => "fake";

        protected override IAsyncEnumerable<FakeMessage> OpenStreamAsync(CancellationToken ct) => _openStream(ct);

        protected override bool IsRelevant(FakeMessage message) => _isRelevant(message);

        protected override Task PublishAsync(FakeMessage message, IMessageBus bus, CancellationToken ct)
        {
            Published.Add(message);
            return bus.PublishAsync(message).AsTask();
        }
    }

    private static (IServiceScopeFactory factory, CountingScopeFactory counting) BuildScopeFactory(IMessageBus bus)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => bus);
        var provider = services.BuildServiceProvider();
        var inner = provider.GetRequiredService<IServiceScopeFactory>();
        var counting = new CountingScopeFactory(inner);
        return (counting, counting);
    }

    [Test]
    public async Task ExecuteAsync_PublishesEachRelevantMessage_CreatingAFreshScopePerMessage()
    {
        var bus = Substitute.For<IMessageBus>();
        var (scopeFactory, counting) = BuildScopeFactory(bus);
        var tcs = new TaskCompletionSource();
        var items = new[] { new FakeMessage("a"), new FakeMessage("b") };

        var service = new RecordingIngestionService(ct => ScriptedStream(items, ct), scopeFactory);
        // Signal once both messages have been recorded, since PublishAsync itself has no return hook here.
        _ = Task.Run(async () =>
        {
            while (service.Published.Count < items.Length)
                await Task.Delay(5);
            tcs.TrySetResult();
        });

        await service.StartAsync(CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.Published.Select(m => m.Value), Is.EqualTo(new[] { "a", "b" }));
        Assert.That(counting.CreateScopeCallCount, Is.EqualTo(2), "one scope should be created per accepted message");
    }

    [Test]
    public async Task ExecuteAsync_FiltersOutMessagesIsRelevantRejects()
    {
        var bus = Substitute.For<IMessageBus>();
        var (scopeFactory, counting) = BuildScopeFactory(bus);
        var items = new[] { new FakeMessage("keep-me"), new FakeMessage("drop-me") };
        var tcs = new TaskCompletionSource();

        var service = new RecordingIngestionService(
            ct => ScriptedStream(items, ct),
            scopeFactory,
            isRelevant: m => m.Value == "keep-me");
        _ = Task.Run(async () =>
        {
            while (service.Published.Count < 1)
                await Task.Delay(5);
            tcs.TrySetResult();
        });

        await service.StartAsync(CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.That(service.Published.Select(m => m.Value), Is.EqualTo(new[] { "keep-me" }));
        // The filtered message never reaches PublishAsync, so it never pays for a DI scope either.
        Assert.That(counting.CreateScopeCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ExecuteAsync_StreamThrows_RecoversAndStopsCleanlyOnCancellationWithoutWaitingOutTheReconnectDelay()
    {
        var bus = Substitute.For<IMessageBus>();
        var (scopeFactory, _) = BuildScopeFactory(bus);
        var service = new RecordingIngestionService(ThrowingStream, scopeFactory);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50); // let the exception path run at least once
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(service.Published, Is.Empty);
    }

    [Test]
    public async Task ExecuteAsync_TokenAlreadyCancelled_NeverEntersTheLoop()
    {
        var bus = Substitute.For<IMessageBus>();
        var (scopeFactory, counting) = BuildScopeFactory(bus);
        var items = new[] { new FakeMessage("never-seen") };
        var service = new RecordingIngestionService(ct => ScriptedStream(items, ct), scopeFactory);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(service.Published, Is.Empty);
        Assert.That(counting.CreateScopeCallCount, Is.EqualTo(0));
    }
}
