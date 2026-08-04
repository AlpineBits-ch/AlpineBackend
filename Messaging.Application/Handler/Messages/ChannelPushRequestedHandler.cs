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

/// <summary>
/// Sends the phone notification for a guild channel message.
///
/// Guild decided *who* (it owns membership, presence and notification settings) and published the
/// resolved recipient list; this handler only does the sending, because Messaging is where the
/// Firebase credentials and PushNotifiaction already live. Duplicating Firebase initialization
/// into Guild purely to avoid one bus hop would mean two services to configure and two places for
/// a credential to be missing.
/// </summary>
[NonTransactional]
public class ChannelPushRequestedHandler
{
    public static async Task Handle(ChannelPushRequested request, IMessageBus bus,
        BlockCache blocks, PrivacySettingsCache privacySettings, IMessageRepository messages,
        ILogger<ChannelPushRequestedHandler> logger)
    {
        if (request.UserIds.Count == 0) return;

        // T0-3, guild-channel half of "the blocker receives no notification from the blocked user".
        // Guild resolved *who is entitled to hear about this message*; whether a particular member
        // has blocked its author is a Social fact Guild does not hold, so it is applied here, at the
        // last point before the notification actually leaves.
        //
        // On the T2-23 hidden cohort Guild deliberately blanks AuthorId, because Messaging resolves
        // the author's *name* from it and that name may not appear in the payload. The block check
        // still needs to know who sent this, so it is recovered from the stored message - Messaging
        // owns message storage, so this is a local read - and used for nothing but the filter below.
        // Without it a blocked member's message would still buzz the blocker's phone; a contentless
        // notification from somebody you blocked is still a notification from somebody you blocked.
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
        // holds even here. What the flag buys is skipping the profile call (there is no author id to
        // resolve) and rendering a generic body rather than an empty one.
        //
        // The per-recipient set is still computed from this service's own cache rather than trusted
        // wholesale from the flag: a recipient who turned HidePushContent on between Guild's split
        // and this send must not get their body anyway, and the two sources can only ever add to
        // each other.
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
