using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Echo.Realtime.LiveKit;

/// <summary>Keeps <see cref="LiveKitRoomRegistry"/> from drifting away from the fleet.</summary>
public sealed class LiveKitReconciler(
    LiveKitRoomClient client,
    LiveKitRoomRegistry registry,
    LiveKitOptions options,
    ILogger<LiveKitReconciler> logger) : BackgroundService
{
    /// <summary>Slow on purpose.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>How long a registry row is left alone after it was written, whatever the fleet says.
    /// Comfortably longer than create-then-join takes, and irrelevant to a room in use.</summary>
    public static readonly TimeSpan CreationGrace = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.IsConfigured)
        {
            logger.LogInformation(
                "LiveKit is not configured on this instance - the room reconciler will not run.");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await ReconcileAsync(ct);
            }
            catch (Exception ex)
            {
                // Never fatal.
                logger.LogWarning(ex, "LiveKit room reconcile pass failed");
            }
        }
    }

    /// <summary>Internal so a test can drive one pass without waiting on <see cref="Interval"/>.</summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        // Leased for slightly less than the interval, so the next tick is always free to run
        // somewhere rather than being skipped by a lease that outlived its own pass.
        if (!await registry.TryClaimSweepAsync(Interval - TimeSpan.FromSeconds(10), ct))
        {
            logger.LogDebug("Another pod holds the LiveKit reconcile lease; skipping this pass");
            return;
        }

        var known = await registry.EntriesAsync(ct);
        if (known.Count == 0) return;

        var dropped = 0;

        foreach (var node in options.Nodes)
        {
            var mine = known
                .Where(e => string.Equals(e.Value, node.Region, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Key)
                .ToList();
            if (mine.Count == 0) continue;

            // Listed once per node rather than per room: the answer is the same set either way and
            // one call is one crossing of the tunnel.
            IReadOnlyList<LiveKitRoom> live;
            try
            {
                live = await client.ListRoomsAsync(node, ct);
            }
            catch (LiveKitControlException ex)
            {
                // A node that cannot be listed is a node whose rooms this pass knows nothing about.
                logger.LogWarning(ex,
                    "Could not list rooms on {Node}; leaving its {Count} registry row(s) alone",
                    node.Region, mine.Count);
                continue;
            }

            var names = live.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var room in mine.Where(r => !names.Contains(r)))
            {
                // The grace is re-checked inside the room's placement lock rather than against the
                // listing taken above, so a room placed since that listing is not deleted out from
                // under the pod that placed it.
                if (await registry.ForgetIfStaleAsync(room, CreationGrace, ct)) dropped++;
            }
        }

        if (dropped > 0)
            logger.LogInformation(
                "LiveKit reconcile dropped {Count} registry row(s) for rooms the fleet no longer has",
                dropped);
    }
}
