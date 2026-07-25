using Isle.Domain;
using Isle.Domain.Aggregates;

namespace Isle.Api.Services;

/// <summary>
/// Periodically re-drives the proximity-voice subscription graph so it converges even when an
/// individual <c>SubscribeMutual</c> push was lost.
///
/// <para>The subscription wiring is otherwise <b>one-shot</b>: a peer relationship is wired the
/// instant the audible-set diff fires (<see cref="VoiceCluster.MovePlayer"/>) or the moment a
/// player publishes their mic (<c>VoiceCloudflareEndpoints.TracksNew</c>). Both push over SignalR
/// with no acknowledgement, and <c>SubscribeMutual</c> only wires a direction whose peer already
/// has a published track. So a push that lands while the recipient's socket isn't ready — or a
/// direction skipped because the other side hadn't published yet — is never retried, leaving the
/// classic "I can hear them, they hear nothing / see 0 nearby" asymmetry until someone changes
/// cells or republishes.</para>
///
/// <para>This service closes that gap: every tick it takes a consistent snapshot of every audible
/// pair and re-issues <c>SubscribeMutual</c> for each. That call is idempotent (it only asks a
/// client to pull a track that exists, and re-sending the same track a client already pulls is a
/// no-op) and symmetric (one call per pair wires both directions), so healthy subscriptions are
/// untouched while any missing direction is restored within one interval. It also naturally
/// re-points a client whose peer republished with a new Cloudflare session.</para>
/// </summary>
public sealed class VoiceSubscriptionReconcileService(
    VoiceCluster cluster,
    IServiceScopeFactory scopeFactory,
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

        // ISfuClient is scoped (RealtimeSfuClient), so resolve it inside a per-tick scope
        // rather than injecting it into this singleton hosted service.
        using var scope = scopeFactory.CreateScope();
        var sfu = scope.ServiceProvider.GetRequiredService<ISfuClient>();

        foreach (var (a, b) in pairs)
            await sfu.SubscribeMutual(a, b);

        logger.LogDebug("Voice subscription reconcile re-drove {PairCount} audible pair(s)", pairs.Count);
    }
}
