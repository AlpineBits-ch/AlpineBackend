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

        public Task<string?> GetActiveTrackId(string playerId) =>
            Task.FromResult(_tracks.TryGet(playerId, out var track) ? track.TrackName : null);

        public async Task SubscribeMutual(string playerIdA, string playerIdB)
        {
            var hasB = _tracks.TryGet(playerIdB, out var trackB);
            var hasA = _tracks.TryGet(playerIdA, out var trackA);

            // Only ask a peer to pull a track that actually exists — pulling a
            // not-yet-published remote track makes Cloudflare reject with 425.
            if (hasB)
                await _hub.Clients.User(playerIdA)
                    .SendAsync(SfuSocketEvents.SubscribeMutual,
                        new SubscribeMutualPayload(playerIdB, trackB.CfSessionId, trackB.TrackName));

            if (hasA)
                await _hub.Clients.User(playerIdB)
                    .SendAsync(SfuSocketEvents.SubscribeMutual,
                        new SubscribeMutualPayload(playerIdA, trackA.CfSessionId, trackA.TrackName));
        }

        public async Task UnsubscribeAll(string playerId, string cellId)
        {
            // TrackIds populated from whatever the client currently has subscribed —
            // if your SFU service tracks this server-side, resolve it here instead of empty.
            await _hub.Clients.User(playerId)
                .SendAsync(SfuSocketEvents.UnsubscribeAll, new UnsubscribeAllPayload(cellId, Array.Empty<string>()));
        }

        public async Task BroadcastPosition(string playerId, IReadOnlyList<string> recipients, float x, float y, float z)
        {
            var payload = new VoicePositionPayload(playerId, x, y, z);
            await _hub.Clients.Users(recipients).SendAsync(SfuSocketEvents.PlayerPosition, payload);
        }
    }
