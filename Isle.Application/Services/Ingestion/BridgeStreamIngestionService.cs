using Wolverine;

namespace Isle.Api.Services.Ingestion;

/// <summary>
/// Base for the bridge SSE ingestion services. Each bridge feed (chat, game events, stats) has
/// exactly <b>one</b> of these: <c>StreamAsync</c> opens a fresh SSE connection per call, so every
/// extra consumer of a feed is another socket against the game server. Ingestion therefore owns the
/// connection and republishes each line onto the bus as a contract event — everything that reacts
/// to in-game activity subscribes to those events instead of opening its own stream.
/// </summary>
/// <typeparam name="TMessage">The bridge model carried by the feed.</typeparam>
public abstract class BridgeStreamIngestionService<TMessage> : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    protected BridgeStreamIngestionService(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Feed name used in log messages, e.g. "chat".</summary>
    protected abstract string StreamName { get; }

    /// <summary>Opens the underlying SSE feed.</summary>
    protected abstract IAsyncEnumerable<TMessage> OpenStreamAsync(CancellationToken ct);

    /// <summary>Translates one bridge message into bus messages and publishes them.</summary>
    protected abstract Task PublishAsync(TMessage message, IMessageBus bus, CancellationToken ct);

    /// <summary>
    /// Cheap pre-filter applied before a DI scope is created. Override on high-frequency feeds to
    /// drop messages nothing is listening for without paying for a scope per line.
    /// </summary>
    protected virtual bool IsRelevant(TMessage message) => true;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("{Stream} stream ingestion started", StreamName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in OpenStreamAsync(ct))
                {
                    if (!IsRelevant(message))
                        continue;

                    // A scope per message: IMessageBus is scoped, and publishing under its own
                    // scope keeps each message's outbox session independent.
                    using var scope = _scopeFactory.CreateScope();
                    var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
                    await PublishAsync(message, bus, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Stream} stream dropped, reconnecting in {Delay}", StreamName, ReconnectDelay);
            }

            // Also covers a clean end-of-stream: the bridge closed the feed and we reconnect.
            try
            {
                await Task.Delay(ReconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
