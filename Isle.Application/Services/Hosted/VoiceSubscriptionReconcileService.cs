using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Periodically re-drives the proximity-voice subscription graph so it converges even when an
/// individual <c>SubscribeMutual</c>/position push was lost, and re-arms clients whose published
/// track the server forgot across a restart.
/// </summary>
public sealed class VoiceSubscriptionReconcileService(
    VoiceCluster cluster,
    VoiceTrackRegistry tracks,
    IServiceScopeFactory scopeFactory,
    ILogger<VoiceSubscriptionReconcileService> logger) : BackgroundService
{
    // Fast enough that a dropped subscribe on join is corrected within a couple of seconds of
    // real-world "why can't they hear me", cheap enough that the re-push fan-out is negligible.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    // Don't re-order a republish more than once per this window — a normal publish completes well
    // inside it, so a client that got the order and is acting on it isn't nagged again.
    private static readonly TimeSpan RepublishCooldown = TimeSpan.FromSeconds(20);

    // One-time, unconditional sweep after this much uptime: long enough to be well past a deploy's
    // preStop drain window (currently 15s) so any restart-era split-brain state has had time to
    // settle, short enough that a genuinely stuck client isn't left silent for long.
    private static readonly TimeSpan StartupForceRepublishDelay = TimeSpan.FromMinutes(1);

    // Players seen track-less last tick, and the last time each was told to republish.
    private HashSet<string> _tracklessLastTick = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRepublishOrder = new();

    // Audible pairs this process has already re-driven at least once.
    private HashSet<(string A, string B)> _pushedPairs = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = RunStartupForceRepublishAsync(ct);

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

    /// <summary>
    /// Fires once, <see cref="StartupForceRepublishDelay"/> after this instance started, and orders
    /// every player currently in the grid without a track to republish — no grace, no cooldown.
    /// </summary>
    private async Task RunStartupForceRepublishAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupForceRepublishDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var players = cluster.GetPlayers();
            if (players.Count == 0)
                return;

            using var scope = scopeFactory.CreateScope();
            var sfu = scope.ServiceProvider.GetRequiredService<ISfuClient>();

            var ordered = 0;
            foreach (var userId in players)
            {
                if (tracks.TryGet(userId, out _))
                    continue;

                await sfu.RequestRepublish(userId);
                ordered++;
            }

            if (ordered > 0)
                logger.LogInformation(
                    "Startup force-republish ({Delay} after start) ordered {Count} still track-less client(s) to republish, bypassing grace/cooldown",
                    StartupForceRepublishDelay, ordered);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup force-republish sweep failed");
        }
    }

    private async Task ReconcileAsync()
    {
        // Snapshot under the grid lock, then do the SFU pushes outside it.
        var players = cluster.GetPlayers();
        var pairs = cluster.GetAudiblePairs(); // each unordered pair once; SubscribeMutual wires both directions
        if (players.Count == 0)
            return;

        // ISfuClient is scoped (RealtimeSfuClient), so resolve it inside a per-tick scope
        // rather than injecting it into this singleton hosted service.
        using var scope = scopeFactory.CreateScope();
        var sfu = scope.ServiceProvider.GetRequiredService<ISfuClient>();

        await OrderRepublishForForgottenTracks(players, sfu);

        // Only push pairs this process hasn't already confirmed — see _pushedPairs doc comment for
        // why we don't just re-blast every audible pair on every tick.
        var newPairs = pairs.Where(p => !_pushedPairs.Contains(p)).ToList();

        foreach (var (a, b) in newPairs)
        {
            await sfu.SubscribeMutual(a, b);

            // Re-seed position too: SubscribeMutual alone only restores the audio-pull wiring.
            if (cluster.TryGetPosition(a, out var posA))
                await sfu.SendPeerPosition(b, a, posA.X, posA.Y, posA.Z, posA.Yaw, posA.Vx, posA.Vy, posA.Vz, posA.TimestampMs);

            if (cluster.TryGetPosition(b, out var posB))
                await sfu.SendPeerPosition(a, b, posB.X, posB.Y, posB.Z, posB.Yaw, posB.Vx, posB.Vy, posB.Vz, posB.TimestampMs);
        }

        // Replace wholesale: anything still audible stays "pushed"; anything that dropped out of
        // range is forgotten, so a future reappearance is treated as new again.
        _pushedPairs = pairs.ToHashSet();

        if (newPairs.Count > 0)
            logger.LogInformation(
                "Voice subscription reconcile pushed {NewCount} newly-observed audible pair(s) ({TotalCount} audible in total)",
                newPairs.Count, pairs.Count);
        else
            logger.LogDebug("Voice subscription reconcile: {TotalCount} audible pair(s), all already confirmed", pairs.Count);
    }

    /// <summary>
    /// A player in the grid with no track in <see cref="VoiceTrackRegistry"/> is the
    /// restart-survivor signature: we know where they are (telemetry) but not how to pull their
    /// audio.
    /// </summary>
    private async Task OrderRepublishForForgottenTracks(IReadOnlyCollection<string> players, ISfuClient sfu)
    {
        var trackless = new HashSet<string>();
        foreach (var userId in players)
            if (!tracks.TryGet(userId, out _))
                trackless.Add(userId);

        var now = DateTimeOffset.UtcNow;
        var ordered = 0;
        foreach (var userId in trackless)
        {
            // Grace: only act on a player track-less across two ticks — skips fresh joiners mid-publish.
            if (!_tracklessLastTick.Contains(userId))
                continue;

            if (_lastRepublishOrder.TryGetValue(userId, out var last) && now - last < RepublishCooldown)
                continue;

            _lastRepublishOrder[userId] = now;
            await sfu.RequestRepublish(userId);
            ordered++;
        }

        // Forget cooldown state for anyone who now has a track (or left), so a future loss re-triggers.
        if (_lastRepublishOrder.Count > 0)
            foreach (var userId in _lastRepublishOrder.Keys.Where(u => !trackless.Contains(u)).ToList())
                _lastRepublishOrder.Remove(userId);

        _tracklessLastTick = trackless;

        if (ordered > 0)
            logger.LogInformation("Ordered {Count} client(s) to republish voice after a forgotten-track detection", ordered);
    }
}
