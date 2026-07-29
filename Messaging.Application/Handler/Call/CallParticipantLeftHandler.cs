using Echo.Realtime;
using Messaging.Application.Services;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Application.Handler.Call;

/// <summary>Notifies the still-connected participants that someone left a call which is still
/// going for the rest of them (see Call.Leave). Only reaches devices actually still connected -
/// participants who declined/left earlier have nothing to do with this call anymore.</summary>
public class CallParticipantLeftHandler
{
    public static async Task Handle(CallParticipantLeft @event, IHubContext<EchoRealtimeHub> hub, IDistributedCache cache)
    {
        var call = await CallService.GetCallById(@event.CallId, cache);
        if (call is null) return;

        var recipientIds = call.Participants
            .Where(p => p.Status == CallStatus.Connected && p.UserId != @event.UserId)
            .Select(p => p.UserId)
            .ToList();
        if (recipientIds.Count == 0) return;

        await hub.Clients.Users(recipientIds).SendAsync("call.CallParticipantLeft", new { callId = @event.CallId, userId = @event.UserId });
    }
}
