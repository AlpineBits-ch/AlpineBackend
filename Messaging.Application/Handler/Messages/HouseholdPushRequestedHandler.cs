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
/// Sends the phone notification for a household module event - a chore falling due, an expense you
/// are on the hook for, a decision waiting on your vote.
/// </summary>
[NonTransactional]
public class HouseholdPushRequestedHandler
{
    public static async Task Handle(HouseholdPushRequested request, IMessageBus bus,
        PrivacySettingsCache privacySettings, ILogger<HouseholdPushRequestedHandler> logger)
    {
        if (request.UserIds.Count == 0) return;

        var tokenResponse = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
            new GetPushTokensForUsersRequest { UserIds = request.UserIds, Kinds = [PushTokenKind.Fcm] });

        // Localization is decided per token, not per user: two handsets on one account can be on
        // different releases, and a key the older build's bundle does not carry renders as the key
        // itself rather than falling back to English.
        var recipients = tokenResponse.Tokens
            .Where(t => t.Kind == PushTokenKind.Fcm)
            .Select(t => new HouseholdPushRecipient(
                t.Token, t.UserId, t.Supports(PushCapabilities.LocalizedV1)))
            .ToList();

        if (recipients.Count == 0) return;

        // T2-23, resolved here rather than trusted from Guild: this service owns the privacy cache,
        // and a recipient who turned HidePushContent on a moment ago must not get their body anyway.
        var settings = await privacySettings.GetAsync(request.UserIds);
        var hideContentFor = settings.Values
            .Where(s => s.HidePushContent)
            .Select(s => s.UserId)
            .ToHashSet(StringComparer.Ordinal);

        await HouseholdPushService.SendAsync(recipients, new HouseholdPushPayload
        {
            GuildId = request.GuildId,
            ChannelId = request.ChannelId,
            Kind = request.Kind,
            TargetId = request.TargetId,
            Title = request.Title,
            Body = request.Body,
            TitleLocKey = request.TitleLocKey,
            TitleLocArgs = request.TitleLocArgs,
            BodyLocKey = request.BodyLocKey,
            BodyLocArgs = request.BodyLocArgs,
            HideContentForUserIds = hideContentFor,
        }, logger);

        logger.LogDebug("Sent {Count} household push notifications for {Kind}",
            recipients.Count, request.Kind);
    }
}
