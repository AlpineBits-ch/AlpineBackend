using Federation.Application.Dtos.Events;
using Federation.Application.Dtos.Events.Bidirectional.Conversation;
using Federation.Application.Dtos.Events.Bidirectional.Guild;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Contracts.Materialization.Conversation;
using Federation.Contracts.Materialization.Guild;
using Federation.Contracts.Materialization.Messaging;
using Federation.Contracts.Materialization.Social;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Federation.Application.Bus.Inbound;

/// <summary>
/// Turns a DAG-resolved inbound FederationEvent into the matching Federation.Contracts
/// materialization command and publishes it - this is what Guild/Messaging/Social.Application's own
/// materialization handlers (Phase 3) subscribe to.
/// </summary>
public static class InboundEventDispatcher
{
    public static async Task DispatchAsync(FederationEvent @event, MicroserviceContext db, IMessageBus bus, CancellationToken ct)
    {
        var originInstance = await db.FederationInstances.FirstOrDefaultAsync(i => i.Host == @event.Host, ct);
        if (originInstance is null) return; // Shouldn't happen: the inbound endpoint already validated the host is registered.

        // The signature on the envelope proves the event came from @event.Host - it says nothing
        // about whose id the payload names.
        if (!IsSenderFromOrigin(@event.SenderId, @event.Host)) return;

        var originInstanceId = originInstance.Id;
        var senderId = @event.SenderId;

        object? command = @event switch
        {
            MessageCreated e => new FederatedMessageCreatedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ChannelId = Strip(e.ChannelId), MessageId = e.MessageId, Content = e.Content },
            MessageEdited e => new FederatedMessageEditedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ChannelId = Strip(e.ChannelId), MessageId = e.MessageId, Content = e.Content },
            MessageDeleted e => new FederatedMessageDeletedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ChannelId = Strip(e.ChannelId), MessageId = e.MessageId },
            MessageReactionAdded e => new FederatedMessageReactionAddedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ChannelId = Strip(e.ChannelId), MessageId = e.MessageId, Emoji = e.Emoji },
            MessageReactionRemoved e => new FederatedMessageReactionRemovedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ChannelId = Strip(e.ChannelId), MessageId = e.MessageId, Emoji = e.Emoji },

            GuildMemberJoined e => new FederatedGuildMemberJoinedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, GuildId = Strip(e.GuildId), UserId = Strip(senderId) },
            GuildMemberLeft e => new FederatedGuildMemberLeftReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, GuildId = Strip(e.GuildId), UserId = Strip(senderId) },
            GuildMemberBanned e => new FederatedGuildMemberBannedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, GuildId = Strip(e.GuildId), BannedUserId = Strip(e.BannedUserId), Reason = e.Reason },
            GuildInviteRedeemed e => new FederatedGuildInviteRedeemedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, GuildId = Strip(e.GuildId), InviteCode = e.InviteCode },
            GuildJoinRequest e => new FederatedGuildJoinRequestReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, GuildId = Strip(e.GuildId) },

            SocialFriendRequest e => new FederatedFriendRequestReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, TargetUserId = Strip(e.TargetUserId) },
            SocialFriendAccepted e => new FederatedFriendAcceptedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, InitiatorUserId = Strip(e.InitiatorUserId) },
            SocialFriendRejected e => new FederatedFriendRejectedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, InitiatorUserId = Strip(e.InitiatorUserId) },
            SocialFriendRemoved e => new FederatedFriendRemovedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, TargetUserId = Strip(e.TargetUserId) },

            ConversationCreated e => new FederatedConversationCreatedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ConversationId = Strip(e.ConversationId), MemberIds = e.MemberIds.Select(Strip).ToArray() },
            ConversationEdited e => new FederatedConversationEditedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ConversationId = Strip(e.ConversationId) },
            ConversationDeleted e => new FederatedConversationDeletedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ConversationId = Strip(e.ConversationId) },
            ConversationMemberAdded e => new FederatedConversationMemberAddedReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ConversationId = Strip(e.ConversationId), UserId = Strip(e.UserId) },
            ConversationMemberLeft e => new FederatedConversationMemberLeftReceived { EventId = e.EventId, OriginInstanceId = originInstanceId, SenderId = senderId, ConversationId = Strip(e.ConversationId), UserId = Strip(e.UserId) },

            // GuildInviteAccepted/GuildInviteRevoked are outbound-only per the venta/v0.1
            // catalogue - a remote instance never sends them to us, so there's nothing to
            // materialize if one somehow arrives.
            _ => null,
        };

        if (command is not null)
            await bus.PublishAsync(command);
    }

    // Inbound ids arrive in wire form (<id>:<ourHost>, addressed to us) - strip the suffix to
    // recover the plain local id per the canonical-ID convention.
    private static string Strip(string federatedOrPlainId)
    {
        var colonIndex = federatedOrPlainId.IndexOf(':');
        return colonIndex < 0 ? federatedOrPlainId : federatedOrPlainId[..colonIndex];
    }

    /// <summary>
    /// A sender id is <c>&lt;userId&gt;:&lt;originHost&gt;</c> (see
    /// UserService.GetFederatedUserId).
    /// </summary>
    private static bool IsSenderFromOrigin(string? senderId, string? originHost)
    {
        if (string.IsNullOrWhiteSpace(senderId)) return false;

        var colonIndex = senderId.IndexOf(':');
        if (colonIndex < 0) return false;

        var qualifier = NormalizeAuthority(senderId[(colonIndex + 1)..]);
        var origin = NormalizeAuthority(originHost);

        return qualifier.Length > 0
               && origin.Length > 0
               && string.Equals(qualifier, origin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reduces a host value - with or without scheme, port, path or trailing slash - to a
    /// bare comparable authority.</summary>
    private static string NormalizeAuthority(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var trimmed = value.Trim().TrimEnd('/');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
            return absolute.Authority;

        // Scheme-less ("origin.example.com" or "origin.example.com/x") - take the first segment.
        var slashIndex = trimmed.IndexOf('/');
        return slashIndex < 0 ? trimmed : trimmed[..slashIndex];
    }
}
