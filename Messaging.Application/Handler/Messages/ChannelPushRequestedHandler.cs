using System.Text;
using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Repositories;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;
using Wolverine.Attributes;

namespace Messaging.Application.Handler.Messages;

/// <summary>Sends the phone notification for a guild channel message.</summary>
[NonTransactional]
public class ChannelPushRequestedHandler
{
    public static async Task Handle(ChannelPushRequested request, IMessageBus bus,
        BlockCache blocks, PrivacySettingsCache privacySettings, IMessageRepository messages,
        ILogger<ChannelPushRequestedHandler> logger)
    {
        if (request.UserIds.Count == 0) return;

        // T0-3, guild-channel half of "the blocker receives no notification from the blocked user".
        var authorId = request.AuthorId;
        if (string.IsNullOrWhiteSpace(authorId))
        {
            var stored = await messages.GetMessageAsync(request.MessageId);
            authorId = stored?.AuthorId ?? string.Empty;

            if (string.IsNullOrWhiteSpace(authorId))
            {
                logger.LogWarning(
                    "Channel push for {MessageId} carries no author id and the message could not be read back; blocks cannot be applied",
                    request.MessageId);
            }
        }

        var userIds = request.UserIds.ToList();

        if (!string.IsNullOrWhiteSpace(authorId))
        {
            var blocked = await blocks.BlockedEitherWayAsync(authorId, userIds);
            if (blocked.Count > 0) userIds = userIds.Where(id => !blocked.Contains(id)).ToList();
        }

        if (userIds.Count == 0) return;

        var tokenResponse = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
            new GetPushTokensForUsersRequest { UserIds = userIds, Kinds = [PushTokenKind.Fcm] });

        // Paired with the owning user id rather than flattened to a token list - the recipient's own
        // id has to travel in the payload so the device can locate that account's MLS state.
        var recipients = tokenResponse.Tokens
            .Where(t => t.Kind == PushTokenKind.Fcm)
            .Select(t => (t.Token, t.UserId))
            .ToList();
        if (recipients.Count == 0) return;

        // T2-23. Guild pre-splits a message's recipients into at most two events and marks the
        // hidden cohort, on which Content is empty and AuthorId is blank - so the privacy property
        // holds even here.
        var settings = await privacySettings.GetAsync(userIds);
        var hideContentFor = request.HideContent
            ? userIds.ToHashSet(StringComparer.Ordinal)
            : settings.Values
                .Where(s => s.HidePushContent)
                .Select(s => s.UserId)
                .ToHashSet(StringComparer.Ordinal);

        var senderName = MessagePushService.HiddenContentTitle;
        string? senderAvatarUrl = null;

        if (!request.HideContent)
        {
            var profile = await bus.InvokeAsync<GetProfileByUserIdResponse>(
                new GetProfileByUserIdRequest { UserId = request.AuthorId });

            senderName = profile.Profile?.UserName ?? "New message";
            senderAvatarUrl = profile.Profile?.AvatarUrl;
        }

        await MessagePushService.SendAsync(recipients, new MessagePushPayload
        {
            HideContentForUserIds = hideContentFor,
            MessageId = request.MessageId,
            ContextId = request.ChannelId,
            ChannelId = request.ChannelId,
            GuildId = request.GuildId,
            AuthorId = request.AuthorId,
            SenderName = senderName,
            SenderAvatarUrl = senderAvatarUrl,
            IsEncrypted = request.IsEncrypted,
            Content = request.Content,
            MlsGeneration = request.MlsGeneration,
        }, logger);

        logger.LogDebug("Sent {TokenCount} channel push notifications for message {MessageId}",
            recipients.Count, request.MessageId);
    }
}
