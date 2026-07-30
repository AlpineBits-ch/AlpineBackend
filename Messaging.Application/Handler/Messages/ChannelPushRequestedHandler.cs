using System.Text;
using Guild.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
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

        var tokenResponse = await bus.InvokeAsync<GetDeviceTokenForUserIdResponse>(
            new GetDeviceTokenForUserIdRequest { UserIds = request.UserIds });

        if (tokenResponse.Tokens.Count == 0) return;

        var profile = await bus.InvokeAsync<GetProfileByUserIdResponse>(
            new GetProfileByUserIdRequest { UserId = request.AuthorId });

        // Same substitution the DM path makes: the server cannot read an encrypted body, so the
        // notification says that a message arrived rather than leaking ciphertext into the tray.
        var body = request.IsEncrypted
            ? "You have a new encrypted message"
            : Encoding.UTF8.GetString(request.Content);

        foreach (var token in tokenResponse.Tokens)
        {
            await PushNotifiaction.SendPushNotification(new PushNotificationParams
            {
                Token = token,
                Title = profile.Profile?.UserName ?? "New message",
                Body = body,
                Data = new Dictionary<string, string>
                {
                    ["guildId"] = request.GuildId,
                    ["channelId"] = request.ChannelId,
                    ["messageId"] = request.MessageId,
                },
            });
        }

        logger.LogDebug("Sent {TokenCount} channel push notifications for message {MessageId}",
            tokenResponse.Tokens.Count, request.MessageId);
    }
}
