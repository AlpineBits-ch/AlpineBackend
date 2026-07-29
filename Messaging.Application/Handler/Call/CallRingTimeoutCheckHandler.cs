using Echo.Realtime;
using Echo.Realtime.Caching;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;

namespace Messaging.Application.Handler.Call;

public class CallRingTimeoutCheckHandler
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(40)
    };

    public static async Task Handle(CallRingTimeoutCheck @event, IHubContext<EchoRealtimeHub> hubContext,
        IDistributedCache cache, LockedJsonCacheStore callStore, IMessageBus bus)
    {
        // Locked: guards the Pending check-then-act against Accept/Decline landing in the same
        // window (e.g. the ring timeout firing right as the callee accepts).
        var didTimeout = false;
        var call = await callStore.UpdateAsync<Domain.Entities.Call>(
            Domain.Entities.Call.GetCacheId(@event.CallId), Domain.Entities.Call.GetCacheId(@event.CallId),
            c =>
            {
                if (c.Status != CallStatus.Pending) return;
                c.Timeout();
                didTimeout = true;
            }, CacheOptions);
        if (call == null || !didTimeout) return; // already answered, declined, or ended

        var participantIds = call.Participants.Select(p => p.UserId).ToList();

        await Task.WhenAll(participantIds.Select(id => cache.RemoveAsync($"user-call:{id}")));

        foreach (var evt in call.GetDomainEvents())
        {
            await bus.PublishAsync(evt);
        }

        await hubContext.Clients.Users(participantIds).SendAsync("call.CallEnded", new { callId = call.Id });

        var callerProfile = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest { UserId = call.CreatorId });
        var deviceTokens = await bus.InvokeAsync<GetDeviceTokenForUserIdResponse>(new GetDeviceTokenForUserIdRequest { UserIds = participantIds });
        var voipTokens = await bus.InvokeAsync<GetVoipTokenForUserIdResponse>(new GetVoipTokenForUserIdRequest { UserIds = participantIds });
        await CallPushService.SendCancelCallAsync(deviceTokens.Tokens, voipTokens.Tokens, new CallPushPayload
        {
            CallId = call.Id,
            ConversationId = call.ConversationId,
            CallerName = callerProfile.Profile?.UserName ?? string.Empty,
            CallerAvatarUrl = callerProfile.Profile?.AvatarUrl,
        });
    }
}
