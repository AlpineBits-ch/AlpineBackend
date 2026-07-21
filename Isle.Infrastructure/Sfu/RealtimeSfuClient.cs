using Echo.Realtime;
using Isle.Api;
using Isle.Contracts.Events.Voice;
using Microsoft.AspNetCore.SignalR;

namespace Isle.Infrastructure.Sfu;


    public class RealtimeSfuClient : ISfuClient
    {
        private readonly IHubContext<EchoRealtimeHub> _hub;

        public RealtimeSfuClient(IHubContext<EchoRealtimeHub> hub) => _hub = hub;

        // existing media/track publish-subscribe implementations live here too...

        public Task<string?> GetActiveTrackId(string playerId)
        {
            throw new NotImplementedException("Wire this up against your existing SFU track registry.");
        }

        public async Task SubscribeMutual(string playerIdA, string playerIdB)
        {
            var trackB = await GetActiveTrackId(playerIdB);
            var trackA = await GetActiveTrackId(playerIdA);

            if (trackB is not null)
                await _hub.Clients.User(playerIdA)
                    .SendAsync(SfuSocketEvents.SubscribeMutual, new SubscribeMutualPayload(playerIdB, trackB));

            if (trackA is not null)
                await _hub.Clients.User(playerIdB)
                    .SendAsync(SfuSocketEvents.SubscribeMutual, new SubscribeMutualPayload(playerIdA, trackA));
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
