using Wolverine;

namespace Isle.Api.Services.Ingestion;

/// <summary>Base for the bridge SSE ingestion services.</summary>
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

    /// <summary>Cheap pre-filter applied before a DI scope is created.</summary>
    protected virtual bool IsRelevant(TMessage message) => true;

    /// <summary>
    /// Runs for every message, before <see cref="IsRelevant"/> and outside any DI scope.
    /// </summary>
    protected virtual Task ObserveAsync(TMessage message, CancellationToken ct) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("{Stream} stream ingestion started", StreamName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in OpenStreamAsync(ct))
                {
                    await ObserveAsync(message, ct);

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
