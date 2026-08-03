using Echo.Realtime;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Messaging.Application.Services;
using Messaging.Domain.Enums;
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

        var cancelRecipientIds = CancelRecipientIds(call, @event.UserId);
        if (cancelRecipientIds.Count > 0)
        {
            var callerProfile = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest { UserId = call.CreatorId });
            var pushTokens = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
                new GetPushTokensForUsersRequest { UserIds = cancelRecipientIds });
            await CallPushService.SendCancelCallAsync(pushTokens.Of(PushTokenKind.Fcm), pushTokens.Of(PushTokenKind.ApnsVoip), new CallPushPayload
            {
                CallId = call.Id,
                ConversationId = call.ConversationId,
                CallerId = call.CreatorId,
                CallerName = callerProfile.Profile?.UserName ?? string.Empty,
                CallerAvatarUrl = callerProfile.Profile?.AvatarUrl,
                ExcludeDeviceId = @event.DeviceId,
                CancelReason = CallCancelReason.DeclinedElsewhere,
            });
        }
    }

    /// <summary>
    /// Which users still have a device ringing after <paramref name="decliningUserId"/> turned the
    /// call down - the invitees who haven't answered, plus the decliner themselves, whose other
    /// devices are still ringing for a call this user has already refused (the declining device
    /// recognises and drops its own copy via <see cref="CallPushPayload.ExcludeDeviceId"/>).
    ///
    /// <para>Never the creator: they placed the call and are in it, and a cancel push there reports
    /// and immediately ends a phantom CallKit call over the live one. When a decline genuinely ends
    /// the call - a 1:1, or the last invitee - <see cref="CallEndNotifier"/> is what tells
    /// everyone, creator included.</para>
    /// </summary>
    public static List<string> CancelRecipientIds(Domain.Entities.Call call, string? decliningUserId) =>
        call.Participants
            .Where(p => p.UserId != call.CreatorId
                        && (p.Status == CallStatus.Pending || p.UserId == decliningUserId))
            .Select(p => p.UserId)
            .Distinct()
            .ToList();
}
