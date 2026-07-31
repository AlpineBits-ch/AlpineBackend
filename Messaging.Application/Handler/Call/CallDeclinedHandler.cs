using Echo.Realtime;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Messaging.Application.Services;
using Messaging.Domain.Events.Call;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;

namespace Messaging.Application.Handler.Call;

public class CallDeclinedHandler
{
    public static async Task Handle(CallDeclined @event, IHubContext<EchoRealtimeHub> hubContext, IDistributedCache cache, IMessageBus bus)
    {
        var call = await CallService.GetCallById(@event.CallId, cache);
        if (call == null)
        {
            throw new Exception("Call not found, cannot end call");
        }

        await hubContext.Clients.Users(call.Participants.Select(p => p.UserId)).SendAsync("call.CallDeclined", call);

        var cancelRecipientIds = call.Participants.Select(p => p.UserId).Where(id => id != @event.UserId).ToList();
        if (cancelRecipientIds.Count > 0)
        {
            var callerProfile = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest { UserId = call.CreatorId });
            var pushTokens = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
                new GetPushTokensForUsersRequest { UserIds = cancelRecipientIds });
            await CallPushService.SendCancelCallAsync(pushTokens.Of(PushTokenKind.Fcm), pushTokens.Of(PushTokenKind.ApnsVoip), new CallPushPayload
            {
                CallId = call.Id,
                ConversationId = call.ConversationId,
                CallerName = callerProfile.Profile?.UserName ?? string.Empty,
                CallerAvatarUrl = callerProfile.Profile?.AvatarUrl,
            });
        }
    }
}
