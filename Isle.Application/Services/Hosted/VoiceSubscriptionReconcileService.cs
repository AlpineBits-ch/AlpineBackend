using Echo.Realtime.LiveKit;
using Isle.Domain;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using Isle.Infrastructure.Sfu;

namespace Isle.Api.Services.Hosted;

/// <summary>
/// Keeps proximity voice converging: refreshes who is actually publishing from the SFU, then
/// re-drives the subscription graph for pairs this process has not yet wired.
/// </summary>
public sealed class VoiceSubscriptionReconcileService(
    VoiceCluster cluster,
    VoiceTrackRegistry tracks,
    IsleVoiceRoom room,
    LiveKitRoomClient livekit,
    IServiceScopeFactory scopeFactory,
    ILogger<VoiceSubscriptionReconcileService> logger) : BackgroundService
{
    // Fast enough that a lost subscribe is corrected within a couple of seconds of real-world "why
    // can't they hear me", cheap enough that one ListParticipants plus the re-push fan-out is
    // negligible.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    // Audible pairs this process has already re-driven at least once.
    private HashSet<(string A, string B)> _pushedPairs = new();

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
                await ReconcileAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Voice subscription reconcile tick failed");
            }
        }
    }

    /// <summary>Internal (rather than private) so unit tests can drive one reconcile pass directly
    /// without waiting on <see cref="Interval"/>.</summary>
    internal async Task ReconcileAsync(CancellationToken ct = default)
    {
        await RefreshPublishersAsync(ct);

        // Snapshot under the grid lock, then do the pushes outside it.
        var players = cluster.GetPlayers();
        var pairs = cluster.GetAudiblePairs(); // each unordered pair once; SubscribeMutual wires both directions
        if (players.Count == 0) return;

        // ISfuClient is scoped, so resolve it inside a per-tick scope rather than injecting it into
        // this singleton hosted service.
        using var scope = scopeFactory.CreateScope();
        var sfu = scope.ServiceProvider.GetRequiredService<ISfuClient>();

        var newPairs = pairs.Where(p => !_pushedPairs.Contains(p)).ToList();

        foreach (var (a, b) in newPairs)
        {
            await sfu.SubscribeMutual(a, b);

            // Re-seed position too: SubscribeMutual alone only restores the audio wiring.
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

    /// <summary>Replaces the published-track map with what the SFU currently has.</summary>
    private async Task RefreshPublishersAsync(CancellationToken ct)
    {
        if (!room.IsConfigured) return;

        try
        {
            if (await room.FindAsync(ct) is not { } node) return;

            var participants = await livekit.ListParticipantsAsync(node, room.Name, ct);

            var live = participants
                .Where(p => !string.Equals(p.State, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
                .Select(p => (Participant: p, Track: p.Tracks.FirstOrDefault(IsMicrophone)))
                .Where(e => e.Track is not null)
                .Select(e => (
                    PlayerId: LiveKitIdentity.UserOf(e.Participant.Identity),
                    Track: new VoiceTrackRegistry.PublishedTrack(
                        e.Participant.Identity, e.Track!.Sid, e.Track.Name ?? "audio")))
                .ToList();

            tracks.Sync(live);
        }
        catch (LiveKitControlException ex)
        {
            logger.LogWarning(ex,
                "Could not list proximity room participants; leaving the published-track map alone");
        }
    }

    private static bool IsMicrophone(LiveKitTrack track) =>
        string.Equals(track.Source, LiveKitSources.Microphone, StringComparison.OrdinalIgnoreCase)
        || string.Equals(track.Name, "audio", StringComparison.OrdinalIgnoreCase);
}
