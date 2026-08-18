using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Wolverine;
using Wolverine.Attributes;

namespace Messaging.Application.Handler.Messages;

/// <summary>
/// Sends the phone notification for a scene's stale turn - the notification a play-by-post game
/// lives on, since a phone that has been closed since yesterday learns nothing from a hub event.
/// </summary>
[NonTransactional]
public class SceneTurnPushRequestedHandler
{
    public static async Task Handle(SceneTurnPushRequested request, IMessageBus bus,
        PrivacySettingsCache privacySettings, ILogger<SceneTurnPushRequestedHandler> logger)
    {
        if (request.UserIds.Count == 0) return;

        var tokenResponse = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
            new GetPushTokensForUsersRequest { UserIds = request.UserIds, Kinds = [PushTokenKind.Fcm] });

        var recipients = tokenResponse.Tokens
            .Where(t => t.Kind == PushTokenKind.Fcm)
            .Select(t => new ScenePushRecipient(t.Token, t.UserId))
            .ToList();

        if (recipients.Count == 0) return;

        // T2-23, resolved here rather than trusted from Guild: this service owns the privacy cache.
        var settings = await privacySettings.GetAsync(request.UserIds);
        var hideContentFor = settings.Values
            .Where(s => s.HidePushContent)
            .Select(s => s.UserId)
            .ToHashSet(StringComparer.Ordinal);

        await ScenePushService.SendAsync(recipients, new ScenePushPayload
        {
            GuildId = request.GuildId,
            ChannelId = request.ChannelId,
            PersonaId = request.PersonaId,
            PersonaName = request.AuthorDisplayName,
            PersonaHidden = request.PersonaHidden,
            SceneName = request.SceneName,
            DeadlineAt = request.TurnDeadlineAt,
            Escalated = request.Escalated,
            HideContentForUserIds = hideContentFor,
        }, logger);

        logger.LogDebug("Sent {Count} scene turn push notifications for {ChannelId}",
            recipients.Count, request.ChannelId);
    }
}
