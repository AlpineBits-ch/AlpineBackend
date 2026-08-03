using Echo.Realtime;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Messaging.Domain.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;

namespace Messaging.Application.Services;

/// <summary>
/// Shared "the call is over, tell everyone" side effects - clears the user-call reverse-lookup
/// keys, broadcasts call.CallEnded, and cancels any still-pending VoIP/push notifications for
/// participants who hadn't answered.
/// </summary>
public static class CallEndNotifier
{
    public static async Task NotifyAsync(
        Domain.Entities.Call call,
        CallEndReason reason,
        string? initiatingUserId,
        IMessageBus bus,
        IDistributedCache cache,
        IHubContext<EchoRealtimeHub> hub)
    {
        var participantIds = call.Participants.Select(p => p.UserId).ToList();

        await Task.WhenAll(participantIds.Select(id => cache.RemoveAsync($"user-call:{id}")));

        await hub.Clients.Users(participantIds).SendAsync("call.CallEnded", new { callId = call.Id, reason = reason.ToString() });

        var cancelRecipientIds = initiatingUserId is null
            ? participantIds
            : participantIds.Where(id => id != initiatingUserId).ToList();
        if (cancelRecipientIds.Count == 0) return;

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
            CancelReason = CallCancelReason.Ended,
        });
    }
}
