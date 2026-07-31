using Echo.Realtime;
using Echo.Realtime.Devices;
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

public class CallAcceptedHandler
{
    public static async Task Handle(CallAccepted @event, IHubContext<EchoRealtimeHub> hubContext, IDistributedCache cache, IMessageBus bus)
    {
        var call = await CallService.GetCallById(@event.CallId, cache);
        if (call == null)
        {
            throw new Exception("Call not found, cannot end call");
        }

        await hubContext.Clients.Users(call.Participants.Select(p => p.UserId)).SendAsync("call.CallAccepted", call);

        var cancelRecipientIds = call.Participants.Select(p => p.UserId).Where(id => id != @event.UserId).ToList();

        // The accepting user's *other* devices are still ringing, so they want the cancel too -
        // only the device that accepted must be spared, or it would dismiss the call it just
        // answered. Push tokens carry their device now, which is what makes that distinction
        // possible; before this, the whole user had to be skipped.
        var acceptingDeviceId = !string.IsNullOrWhiteSpace(@event.DeviceId) && @event.DeviceId != DeviceIdentity.DefaultDeviceId
            ? @event.DeviceId
            : null;
        if (acceptingDeviceId is not null) cancelRecipientIds.Add(@event.UserId);

        if (cancelRecipientIds.Count > 0)
        {
            var callerProfile = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest { UserId = call.CreatorId });
            var pushTokens = await bus.InvokeAsync<GetPushTokensForUsersResponse>(new GetPushTokensForUsersRequest
            {
                UserIds = cancelRecipientIds,
            });

            var recipients = CancelRecipients(pushTokens.Tokens, @event.UserId, acceptingDeviceId);

            await CallPushService.SendCancelCallAsync(
                recipients.Where(t => t.Kind == PushTokenKind.Fcm).Select(t => t.Token),
                recipients.Where(t => t.Kind == PushTokenKind.ApnsVoip).Select(t => t.Token),
                new CallPushPayload
                {
                    CallId = call.Id,
                    ConversationId = call.ConversationId,
                    CallerName = callerProfile.Profile?.UserName ?? string.Empty,
                    CallerAvatarUrl = callerProfile.Profile?.AvatarUrl,
                });
        }
    }

    /// <summary>
    /// Who still needs the "stop ringing" push once <paramref name="acceptingUserId"/> has answered
    /// on <paramref name="acceptingDeviceId"/>.
    ///
    /// <para>Other participants get everything they have. The accepting user is the delicate case:
    /// only tokens that <em>prove</em> they belong to one of their other devices qualify. Deferring
    /// to the request's ExcludeClientDeviceIds would not do - it leaves tokens with no device
    /// attached alone, which is right in general (nothing says whether an unattributed token is the
    /// excluded installation) but wrong here. Every token is unattributed until clients start
    /// sending a device id at registration, so the accepting device would be told to cancel the
    /// call it just picked up.</para>
    /// </summary>
    public static List<PushTokenResponse> CancelRecipients(
        IEnumerable<PushTokenResponse> tokens, string acceptingUserId, string? acceptingDeviceId) =>
        tokens.Where(t => t.UserId != acceptingUserId
                          || (t.ClientDeviceId is not null && t.ClientDeviceId != acceptingDeviceId))
            .ToList();
}
