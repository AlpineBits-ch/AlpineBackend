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
/// Puts a voice-channel invitation in the conversation the two people already have, opening one if
/// they have none.
/// </summary>
public class WriteVoiceInviteMessageHandler
{
    public static async Task<WriteVoiceInviteMessageResponse> Handle(
        WriteVoiceInviteMessage request,
        DirectConversationResolver conversations,
        IMessageBus bus,
        ILogger<WriteVoiceInviteMessageHandler> logger)
    {
        if (string.IsNullOrWhiteSpace(request.InviterId) || string.IsNullOrWhiteSpace(request.TargetUserId))
            return Refused(WriteVoiceInviteMessageResponse.Unavailable);

        var resolved = await conversations.ResolveAsync(request.InviterId, request.TargetUserId);

        if (!resolved.HasConversation)
        {
            // Every outcome here is a settled answer about these two accounts rather than a
            // transient failure, so there is nothing to retry.
            logger.LogDebug(
                "No direct conversation for a voice invite ({InviterId} -> {TargetUserId}): {Outcome}",
                request.InviterId, request.TargetUserId, resolved.Outcome);

            return Refused(resolved.Outcome == DirectConversationOutcome.Refused
                ? WriteVoiceInviteMessageResponse.RecipientPolicy
                : WriteVoiceInviteMessageResponse.Unavailable);
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

        return new WriteVoiceInviteMessageResponse {Written = true, ConversationId = resolved.ConversationId};
    }

    private static WriteVoiceInviteMessageResponse Refused(string refusal) =>
        new() {Written = false, Refusal = refusal};

    /// <summary>The card itself.</summary>
    private static EmbedPayload Card(WriteVoiceInviteMessage request) => EmbedLimits.Clamp(new EmbedPayload
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
