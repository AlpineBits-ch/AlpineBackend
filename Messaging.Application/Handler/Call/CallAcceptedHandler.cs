using Echo.Realtime;
using Echo.Realtime.Devices;
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

        var cancelRecipientIds = CancelRecipientIds(call, @event.UserId);
        var acceptingDeviceId = !string.IsNullOrWhiteSpace(@event.DeviceId) && @event.DeviceId != DeviceIdentity.DefaultDeviceId
            ? @event.DeviceId
            : null;

        if (cancelRecipientIds.Count > 0)
        {
            var callerProfile = await bus.InvokeAsync<GetProfileByUserIdResponse>(new GetProfileByUserIdRequest { UserId = call.CreatorId });
            var pushTokens = await bus.InvokeAsync<GetPushTokensForUsersResponse>(new GetPushTokensForUsersRequest
            {
                UserIds = cancelRecipientIds,
            });

            var recipients = CancelRecipients(pushTokens.Tokens, acceptingDeviceId);

            await CallPushService.SendCancelCallAsync(
                recipients.Where(t => t.Kind == PushTokenKind.Fcm).Select(t => t.Token),
                recipients.Where(t => t.Kind == PushTokenKind.ApnsVoip).Select(t => t.Token),
                new CallPushPayload
                {
                    CallId = call.Id,
                    ConversationId = call.ConversationId,
                    CallerId = call.CreatorId,
                    CallerName = callerProfile.Profile?.UserName ?? string.Empty,
                    CallerAvatarUrl = callerProfile.Profile?.AvatarUrl,
                    ExcludeDeviceId = acceptingDeviceId,
                    CancelReason = CallCancelReason.AcceptedElsewhere,
                });
        }
    }

    /// <summary>
    /// Which <em>users</em> still have a device ringing once <paramref name="acceptingUserId"/> has
    /// answered: invitees that haven't resolved the call yet, plus the accepting user themselves -
    /// accepting flipped their participant row to Connected, but only on one device, and the rest
    /// are ringing just as loudly as anyone else's.
    ///
    /// <para>The creator is deliberately never in here. They placed the call and are sitting in it;
    /// a cancel push reaching them reports and immediately ends a phantom CallKit call on top of
    /// the live one. That was the effect of the previous "every participant except the accepter"
    /// rule, which in a 1:1 call names precisely the caller and nobody else.</para>
    ///
    /// <para>The accepting user is included whether or not their client sent a usable device id.
    /// Gating on that (as this used to) means a client the server cannot place - a desktop, a build
    /// predating the header - leaves that user's other handsets ringing indefinitely.</para>
    /// </summary>
    public static List<string> CancelRecipientIds(Domain.Entities.Call call, string? acceptingUserId)
    {
        var ids = call.Participants
            .Where(p => p.UserId != call.CreatorId && p.Status == CallStatus.Pending)
            .Select(p => p.UserId)
            .ToList();
        if (acceptingUserId is not null && acceptingUserId != call.CreatorId) ids.Add(acceptingUserId);
        return ids.Distinct().ToList();
    }

    /// <summary>
    /// Which of those users' <em>tokens</em> to actually send to, once the call has been answered
    /// on <paramref name="acceptingDeviceId"/>.
    ///
    /// <para>Everything except tokens that <em>prove</em> they belong to the device that just
    /// answered. Tokens with no device attached are kept, and that is the point: they used to be
    /// dropped for the accepting user, on the theory that one of them might be the accepting
    /// device's own - but every token registered before the device-identity consolidation is
    /// unattributed, so in practice that spared the whole account and left the user's other
    /// handsets ringing after they picked up elsewhere. The accepting device now filters its own
    /// cancel out client-side via <see cref="CallPushPayload.ExcludeDeviceId"/>, which it can do
    /// exactly rather than by inference.</para>
    /// </summary>
    public static List<PushTokenResponse> CancelRecipients(
        IEnumerable<PushTokenResponse> tokens, string? acceptingDeviceId) =>
        tokens.Where(t => acceptingDeviceId is null || t.ClientDeviceId != acceptingDeviceId).ToList();
}
