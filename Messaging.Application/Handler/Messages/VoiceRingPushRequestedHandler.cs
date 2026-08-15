using Guild.Contracts.Bus.Events;
using Identity.Contracts;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Wolverine;
using Wolverine.Attributes;

namespace Messaging.Application.Handler.Messages;

/// <summary>
/// Sends the phone notification for a voice-channel ring, and the silent one that takes it away
/// again.
/// </summary>
[NonTransactional]
public class VoiceRingPushRequestedHandler
{
    public static async Task Handle(VoiceRingPushRequested request, IMessageBus bus,
        PrivacySettingsCache privacySettings, ILogger<VoiceRingPushRequestedHandler> logger)
    {
        if (string.IsNullOrWhiteSpace(request.TargetUserId)) return;

        var tokenResponse = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
            new GetPushTokensForUsersRequest { UserIds = [request.TargetUserId], Kinds = [PushTokenKind.Fcm] });

        var rows = tokenResponse.Tokens.Where(t => t.Kind == PushTokenKind.Fcm);

        // The cancel draws nothing, so it must only go where a silent payload is actually delivered
        // silently.
        if (request.Cancel) rows = rows.Where(t => t.CanReceiveSilentPush);

        // Localization is decided per token, not per user: two handsets on one account can be on
        // different releases, and a key the older bundle does not carry renders as the key itself
        // rather than falling back to English.
        var recipients = rows
            .Select(t => new VoiceRingPushRecipient(
                t.Token, t.UserId, t.Supports(PushCapabilities.LocalizedV1)))
            .ToList();

        if (recipients.Count == 0) return;

        // T2-23, resolved here rather than trusted from Guild: this service owns the privacy cache,
        // and somebody who turned HidePushContent on a moment ago must not have a channel name and a
        // person's name land on their lock screen anyway.
        var settings = await privacySettings.GetAsync([request.TargetUserId]);
        var hidden = settings.Values.Any(s => s.HidePushContent);

        await VoiceRingPushService.SendAsync(recipients, new VoiceRingPushPayload
        {
            RingId = request.RingId,
            GuildId = request.GuildId,
            ChannelId = request.ChannelId,
            InviterId = request.InviterId,
            InviterAvatarUrl = request.InviterAvatarUrl,
            ExpiresInSeconds = request.ExpiresInSeconds,
            Title = request.Title,
            Body = request.Body,
            BodyLocKey = request.BodyLocKey,
            BodyLocArgs = request.BodyLocArgs,
            Hidden = hidden,
            Cancel = request.Cancel,
            CancelReason = request.CancelReason,
            ExcludeDeviceId = request.ExcludeDeviceId,
        }, logger);

        logger.LogDebug("Sent {Count} voice-ring push(es) for {RingId} (cancel={Cancel})",
            recipients.Count, request.RingId, request.Cancel);
    }
}
