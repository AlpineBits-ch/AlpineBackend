using System.Text;
using Bots.Contracts.Gateway.Payloads;
using Guild.Contracts.Bus.Events;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Previews;
using Wolverine;
using ContractMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Messaging.Application.Handler.Messages;

/// <summary>
/// Leaves a voice invitation in the conversation the two people already have, so that it is still
/// there after the ring itself has lapsed.
/// </summary>
public class VoiceRingDirectMessageRequestedHandler
{
    public static async Task Handle(
        VoiceRingDirectMessageRequested request,
        DirectConversationResolver conversations,
        IMessageBus bus,
        ILogger<VoiceRingDirectMessageRequestedHandler> logger)
    {
        if (string.IsNullOrWhiteSpace(request.InviterId) || string.IsNullOrWhiteSpace(request.TargetUserId))
            return;

        var resolved = await conversations.ResolveAsync(request.InviterId, request.TargetUserId);

        if (!resolved.HasConversation)
        {
            // Not an error and not retried.
            logger.LogDebug(
                "No direct conversation for voice ring {RingId} ({InviterId} -> {TargetUserId}): {Outcome}",
                request.RingId, request.InviterId, request.TargetUserId, resolved.Outcome);
            return;
        }

        await bus.InvokeAsync(new CreateMessageCommand
        {
            ConversationId = resolved.ConversationId,
            AuthorId = request.InviterId,
            AuthorIdType = AuthorIdType.User,
            Type = ContractMessageType.VoiceChannelInvite,
            // The same sentence the push notification carries, for the same reason the guild-join
            // message carries one: bots, exports and search see Content and nothing else.
            Content = Encoding.UTF8.GetBytes($"Asked you to join {request.ChannelName}"),
            Mentions = [],
            EmbedsJson = GeneratedEmbeds.Serialize([Card(request)]),
        });
    }

    /// <summary>The card itself.</summary>
    private static EmbedPayload Card(VoiceRingDirectMessageRequested request) => EmbedLimits.Clamp(new EmbedPayload
    {
        Type = EmbedTypes.VentaVoiceInvite,
        Title = request.ChannelName,
        Description = "You have been invited to join this voice channel.",
        Flags = EmbedFlags.ServerGenerated,
        Venta = new EmbedVentaPayload
        {
            Kind = "voice_invite",
            Resolved = true,
            RingId = request.RingId,
            GuildId = request.GuildId,
            ChannelId = request.ChannelId,
            ChannelName = request.ChannelName,
            InviterId = request.InviterId,
            ExpiresAt = request.ExpiresAt,
        },
    });
}
