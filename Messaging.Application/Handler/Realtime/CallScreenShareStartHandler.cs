using System.Text.Json;
using Echo.Realtime;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Handler.Realtime;

public class CallScreenShareStartHandler
{
    public static async Task Handle(CallScreenShareStartCommand cmd, IDistributedCache cache, IHubContext<EchoRealtimeHub> hub)
    {
        var raw = await cache.GetStringAsync(Domain.Entities.Call.GetCacheId(cmd.CallId));
        if (raw is null) return;

        var call = JsonSerializer.Deserialize<Domain.Entities.Call>(raw)!;
        var otherIds = call.Participants
            .Where(p => p.UserId != cmd.UserId)
            .Select(p => p.UserId)
            .ToList();

        var cfSessionId = call.Participants
            .FirstOrDefault(p => p.UserId == cmd.UserId)?.CfSessionId;

        await hub.Clients.Users(otherIds).SendAsync("call.ScreenShareStarted",
            new { shareId = cmd.ShareId, userId = cmd.UserId, cfSessionId, trackName = cmd.TrackName });
    }
}
