using System.Text.Json;
using Echo.Realtime;
using Messaging.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Handler.Realtime;

public class CallScreenShareStopHandler
{
    public static async Task Handle(CallScreenShareStopCommand cmd, IDistributedCache cache, IHubContext<EchoRealtimeHub> hub)
    {
        var raw = await cache.GetStringAsync(Domain.Entities.Call.GetCacheId(cmd.CallId));
        if (raw is null) return;

        var call = JsonSerializer.Deserialize<Domain.Entities.Call>(raw)!;
        var otherIds = call.Participants
            .Where(p => p.UserId != cmd.UserId)
            .Select(p => p.UserId)
            .ToList();

        await hub.Clients.Users(otherIds).SendAsync("call.ScreenShareStopped",
            new { shareId = cmd.ShareId });
    }
}
