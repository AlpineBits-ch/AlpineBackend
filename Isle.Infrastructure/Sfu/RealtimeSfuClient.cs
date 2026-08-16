using Echo.Realtime;
using Echo.Realtime.LiveKit;
using Isle.Domain;
using Isle.Domain.Entity.Voice;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Isle.Infrastructure.Sfu;

/// <summary>
/// Proximity voice's push side: the hub events the game client builds its spatial audio graph from,
/// plus the server-side subscription calls that make those events more than a suggestion.
/// </summary>
public class RealtimeSfuClient(
    IHubContext<EchoRealtimeHub> hub,
    VoiceTrackRegistry tracks,
    IsleVoiceRoom room,
    LiveKitRoomClient livekit,
    ILogger<RealtimeSfuClient> logger) : ISfuClient
{
    public Task<string?> GetActiveTrackId(string userId) =>
        Task.FromResult(tracks.TryGet(userId, out var track) ? track.TrackName : null);

    public async Task SubscribeMutual(string userIdA, string userIdB)
    {
        var hasB = tracks.TryGet(userIdB, out var trackB);
        var hasA = tracks.TryGet(userIdA, out var trackA);

        // Only tell a client about a peer who is actually publishing.
        if (hasB)
        {
            await hub.Clients.User(userIdA).SendAsync(SfuSocketEvents.SubscribeMutual,
                new SubscribeMutualPayload(userIdB, trackB.Identity, trackB.TrackName, trackB.TrackSid));
        }

        if (hasA)
        {
            await hub.Clients.User(userIdB).SendAsync(SfuSocketEvents.SubscribeMutual,
                new SubscribeMutualPayload(userIdA, trackA.Identity, trackA.TrackName, trackA.TrackSid));
        }

        // The half the client cannot skip.
        if (hasB) await SubscribeAsync(userIdA, trackB.TrackSid, subscribe: true);
        if (hasA) await SubscribeAsync(userIdB, trackA.TrackSid, subscribe: true);
    }

    public async Task UnsubscribePair(string userIdA, string userIdB)
    {
        // Audibility is symmetric, so when one peer walks out of the other's 3x3 block (or leaves
        // voice) the relationship ends on both sides - tell each to drop the other.
        await hub.Clients.User(userIdA)
            .SendAsync(SfuSocketEvents.PeerLeft, new PeerLeftPayload(userIdB));
        await hub.Clients.User(userIdB)
            .SendAsync(SfuSocketEvents.PeerLeft, new PeerLeftPayload(userIdA));

        // And unsubscribe them at the SFU, which is what keeps proximity from being worth only the
        // client's cooperation.
        if (tracks.TryGet(userIdB, out var trackB))
            await SubscribeAsync(userIdA, trackB.TrackSid, subscribe: false);

        if (tracks.TryGet(userIdA, out var trackA))
            await SubscribeAsync(userIdB, trackA.TrackSid, subscribe: false);
    }

    public async Task BroadcastPosition(string userId, IReadOnlyList<string> recipients, float x, float y, float z, float yaw, float vx, float vy, float vz, long timestampMs)
    {
        var payload = new VoicePositionPayload(userId, x, y, z, yaw, vx, vy, vz, timestampMs);
        await hub.Clients.Users(recipients).SendAsync(SfuSocketEvents.PlayerPosition, payload);
    }

    public async Task SendSelfPosition(string userId, float x, float y, float z, float yaw, float vx, float vy, float vz, long timestampMs)
    {
        await hub.Clients.User(userId)
            .SendAsync(SfuSocketEvents.SelfPosition, new SelfPositionPayload(x, y, z, yaw, vx, vy, vz, timestampMs));
    }

    public async Task SendPeerPosition(string recipientUserId, string peerUserId, float x, float y, float z, float yaw, float vx, float vy, float vz, long timestampMs)
    {
        // Reuses the PlayerPosition event so the client needs no new handler - it just receives one
        // peer's position immediately on subscribe instead of waiting for the peer's next throttled
        // movement broadcast (which never comes if they're standing still).
        await hub.Clients.User(recipientUserId)
            .SendAsync(SfuSocketEvents.PlayerPosition, new VoicePositionPayload(peerUserId, x, y, z, yaw, vx, vy, vz, timestampMs));
    }

    /// <summary>Drives one listener's subscription to one track.</summary>
    private async Task SubscribeAsync(string listenerId, string trackSid, bool subscribe)
    {
        try
        {
            if (await room.FindAsync() is not { } node) return;

            await livekit.UpdateSubscriptionsAsync(
                node, room.Name, listenerId, [trackSid], subscribe);
        }
        catch (LiveKitControlException ex)
        {
            logger.LogWarning(ex,
                "Could not {Action} {Listener} to track {Track} in {Room}",
                subscribe ? "subscribe" : "unsubscribe", listenerId, trackSid, room.Name);
        }
    }
}
