using Echo.Realtime;
using Isle.Api;
using Isle.Api.Voice;
using Isle.Contracts.Events.Voice;
using Microsoft.AspNetCore.SignalR;

namespace Isle.Infrastructure.Sfu;


    public class RealtimeSfuClient : ISfuClient
    {
        private readonly IHubContext<EchoRealtimeHub> _hub;
        private readonly VoiceTrackRegistry _tracks;

        public RealtimeSfuClient(IHubContext<EchoRealtimeHub> hub, VoiceTrackRegistry tracks)
        {
            _hub = hub;
            _tracks = tracks;
        }

        public Task<string?> GetActiveTrackId(string userId) =>
            Task.FromResult(_tracks.TryGet(userId, out var track) ? track.TrackName : null);

        public async Task SubscribeMutual(string userIdA, string userIdB)
        {
            var hasB = _tracks.TryGet(userIdB, out var trackB);
            var hasA = _tracks.TryGet(userIdA, out var trackA);

            // Only ask a peer to pull a track that actually exists — pulling a
            // not-yet-published remote track makes Cloudflare reject with 425.
            if (hasB)
                await _hub.Clients.User(userIdA)
                    .SendAsync(SfuSocketEvents.SubscribeMutual,
                        new SubscribeMutualPayload(userIdB, trackB.CfSessionId, trackB.TrackName));

            if (hasA)
                await _hub.Clients.User(userIdB)
                    .SendAsync(SfuSocketEvents.SubscribeMutual,
                        new SubscribeMutualPayload(userIdA, trackA.CfSessionId, trackA.TrackName));
        }

        public async Task UnsubscribePair(string userIdA, string userIdB)
        {
            // Audibility is symmetric, so when one peer walks out of the other's 3x3 block (or
            // leaves voice) the relationship ends on both sides — tell each to drop the other.
            await _hub.Clients.User(userIdA)
                .SendAsync(SfuSocketEvents.PeerLeft, new PeerLeftPayload(userIdB));
            await _hub.Clients.User(userIdB)
                .SendAsync(SfuSocketEvents.PeerLeft, new PeerLeftPayload(userIdA));
        }

        public async Task BroadcastPosition(string userId, IReadOnlyList<string> recipients, float x, float y, float z, float yaw, float vx, float vy, float vz, long timestampMs)
        {
            var payload = new VoicePositionPayload(userId, x, y, z, yaw, vx, vy, vz, timestampMs);
            await _hub.Clients.Users(recipients).SendAsync(SfuSocketEvents.PlayerPosition, payload);
        }

        public async Task SendSelfPosition(string userId, float x, float y, float z, float yaw, float vx, float vy, float vz, long timestampMs)
        {
            await _hub.Clients.User(userId)
                .SendAsync(SfuSocketEvents.SelfPosition, new SelfPositionPayload(x, y, z, yaw, vx, vy, vz, timestampMs));
        }

        public async Task SendPeerPosition(string recipientUserId, string peerUserId, float x, float y, float z, float yaw, float vx, float vy, float vz, long timestampMs)
        {
            // Reuses the PlayerPosition event so the client needs no new handler — it just
            // receives one peer's position immediately on subscribe instead of waiting for the
            // peer's next throttled movement broadcast (which never comes if they're standing still).
            await _hub.Clients.User(recipientUserId)
                .SendAsync(SfuSocketEvents.PlayerPosition, new VoicePositionPayload(peerUserId, x, y, z, yaw, vx, vy, vz, timestampMs));
        }
    }
