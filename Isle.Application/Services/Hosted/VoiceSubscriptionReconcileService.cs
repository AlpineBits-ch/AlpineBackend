using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Periodically re-drives the proximity-voice subscription graph so it converges even when an
/// individual <c>SubscribeMutual</c>/position push was lost, and re-arms clients whose published
/// track the server forgot across a restart.
///
/// <para><b>Missed subscribe/position.</b> The subscription wiring is otherwise <b>one-shot</b>: a
/// peer relationship is wired the instant the audible-set diff fires (<see cref="VoiceCluster.MovePlayer"/>)
/// or the moment a player publishes their mic (<c>VoiceCloudflareEndpoints.TracksNew</c>). Both push
/// over SignalR with no acknowledgement, and <c>SubscribeMutual</c> only wires a direction whose peer
/// already has a published track. So a push that lands while the recipient's socket isn't ready — or
/// a direction skipped because the other side hadn't published yet — is never retried, leaving the
/// classic "I can hear them, they hear nothing / see 0 nearby" asymmetry until someone changes cells
/// or republishes. This is not just a slow-client problem: during a rolling deploy the outgoing and
/// incoming Isle.Application pods briefly run side by side (the preStop drain window) and both
/// independently consume the same durable position queue as competing consumers, so the two pods'
/// in-memory <see cref="VoiceCluster"/> grids can diverge for that window — one pod's diff can fire
/// and push a position while the other never sees the pairing at all.
///
/// <c>SubscribeMutual</c> is not free on the client: per the frontend integration guide it can
/// trigger a full WebRTC renegotiation, so this deliberately does <b>not</b> re-blast every
/// currently-audible pair on every tick — that would force needless renegotiation churn on an
/// already-healthy graph forever. Instead each pair is re-driven (subscribe + both sides' last-known
/// position) at most <b>once per process lifetime</b>, the moment it's first observed by this
/// instance. That's still a full fix for the restart scenario: a freshly-started pod's tracking set
/// is empty, so every pair audible in its own grid looks new and gets pushed within one interval of
/// it first appearing — without nagging pairs this process has already confirmed.</para>
///
/// <para><b>Lost track after a restart.</b> The SignalR hub is terminated on the Echo gateway, not on
/// this service, so an Isle restart wipes the in-memory <see cref="VoiceTrackRegistry"/> without
/// dropping any client socket. A client that stayed connected never reconnects and so never
/// republishes — position telemetry rebuilds the grid (peers get told to pull <i>its</i> newly-joined
/// neighbours) but nothing rebuilds its own track record, so nobody can pull it back ("he sees me, I
/// don't see him"). This service detects players that are in the grid but have no registered track and
/// sends them a <c>RepublishVoice</c> order. A one-tick grace avoids nagging clients that are simply
/// mid-publish on a normal join, and a per-user cooldown avoids spamming a slow client. That grace and
/// cooldown are heuristics, though, and can in principle keep deferring a given user's order across
/// several ticks if their trackless-ness keeps getting reset (e.g. by more grid churn during a
/// restart). As a backstop, once per process lifetime — <see cref="StartupForceRepublishDelay"/>
/// after startup, comfortably past any deploy's dual-pod overlap — every still-trackless player in the
/// grid is force-ordered to republish unconditionally, bypassing grace and cooldown entirely.</para>
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

    // Players seen track-less last tick, and the last time each was told to republish. Only touched
    // from the single reconcile loop, so plain collections are safe.
    private HashSet<string> _tracklessLastTick = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRepublishOrder = new();

    // Audible pairs this process has already re-driven at least once. A pair is pushed only the
    // first time this instance observes it — re-sending SubscribeMutual to an already-healthy pair
    // is not free client-side (can trigger a WebRTC renegotiation), so this avoids nagging a stable
    // pair every 5s forever while still guaranteeing full recovery within one interval of a restart
    // (a fresh process starts with this empty, so every currently-audible pair looks new once).
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
    /// every player currently in the grid without a track to republish — no grace, no cooldown. A
    /// backstop for the restart-split-brain scenario: it doesn't matter how the trackless state arose
    /// or whether the per-tick heuristic kept deferring it, this runs exactly once per process and
    /// clears it regardless.
    /// </summary>
    /// <summary>Internal (rather than private) so unit tests can drive this directly without waiting on <see cref="StartupForceRepublishDelay"/>.</summary>
    internal async Task RunStartupForceRepublishAsync(CancellationToken ct)
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

    /// <summary>Internal (rather than private) so unit tests can drive one reconcile pass directly without waiting on <see cref="Interval"/>.</summary>
    internal async Task ReconcileAsync()
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
            // The pairing's position payload is otherwise only ever sent once, from whichever
            // side's PeerBecameAudibleEvent diff happened to fire it (see class doc comment) — if
            // that push was lost, or fired on a pod whose grid never converged with this one, the
            // recipient has a working audio subscription but no idea where the peer actually is.
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
    /// A player in the grid with no track in <see cref="VoiceTrackRegistry"/> is the restart-survivor
    /// signature: we know where they are (telemetry) but not how to pull their audio. Tell them to
    /// republish. Requires the player to have been track-less for two consecutive ticks so a normal
    /// joiner still finishing its first publish is left alone, and rate-limits per user.
    /// </summary>
    internal async Task OrderRepublishForForgottenTracks(IReadOnlyCollection<string> players, ISfuClient sfu)
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
