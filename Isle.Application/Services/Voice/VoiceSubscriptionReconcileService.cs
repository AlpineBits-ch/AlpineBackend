using Isle.Domain;
using Isle.Domain.Aggregates;

namespace Isle.Api.Services;

/// <summary>
/// Periodically re-drives the proximity-voice subscription graph so it converges even when an
/// individual <c>SubscribeMutual</c> push was lost.
/// </summary>
public sealed class VoiceSubscriptionReconcileService(
    VoiceCluster cluster,
    ISfuClient sfu,
    ILogger<VoiceSubscriptionReconcileService> logger) : BackgroundService
{
    // Fast enough that a dropped subscribe on join is corrected within a couple of seconds of
    // real-world "why can't they hear me", cheap enough that the re-push fan-out is negligible.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ReconcileAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Voice subscription reconcile tick failed");
            }
        }
    }

    private async Task ReconcileAsync()
    {
        // Snapshot under the grid lock, then do the SFU pushes outside it — GetAudiblePairs
        // returns each pair once, and SubscribeMutual wires both directions.
        var pairs = cluster.GetAudiblePairs();
        if (pairs.Count == 0)
            return;

        foreach (var (a, b) in pairs)
            await sfu.SubscribeMutual(a, b);

        logger.LogDebug("Voice subscription reconcile re-drove {PairCount} audible pair(s)", pairs.Count);
    }
}
