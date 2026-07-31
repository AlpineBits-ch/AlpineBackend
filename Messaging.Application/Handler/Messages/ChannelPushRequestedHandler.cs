using System.Text;
using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Enums;
using Messaging.Application.Services;
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
        ILogger<ChannelPushRequestedHandler> logger)
    {
        if (request.UserIds.Count == 0) return;

        var tokenResponse = await bus.InvokeAsync<GetPushTokensForUsersResponse>(
            new GetPushTokensForUsersRequest { UserIds = request.UserIds, Kinds = [PushTokenKind.Fcm] });

        // Paired with the owning user id rather than flattened to a token list - the recipient's own
        // id has to travel in the payload so the device can locate that account's MLS state.
        var recipients = tokenResponse.Tokens
            .Where(t => t.Kind == PushTokenKind.Fcm)
            .Select(t => (t.Token, t.UserId))
            .ToList();
        if (recipients.Count == 0) return;

        var profile = await bus.InvokeAsync<GetProfileByUserIdResponse>(
            new GetProfileByUserIdRequest { UserId = request.AuthorId });

        await MessagePushService.SendAsync(recipients, new MessagePushPayload
        {
            MessageId = request.MessageId,
            ContextId = request.ChannelId,
            ChannelId = request.ChannelId,
            GuildId = request.GuildId,
            AuthorId = request.AuthorId,
            SenderName = profile.Profile?.UserName ?? "New message",
            SenderAvatarUrl = profile.Profile?.AvatarUrl,
            IsEncrypted = request.IsEncrypted,
            Content = request.Content,
            MlsGeneration = request.MlsGeneration,
        }, logger);

        logger.LogDebug("Sent {TokenCount} channel push notifications for message {MessageId}",
            recipients.Count, request.MessageId);
    }
}
