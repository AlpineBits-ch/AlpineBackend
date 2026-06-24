using Federation.Application.Dtos.Events;

namespace Federation.Application.Providers;

public interface IFederationProvider
{
    FederationProtocolVersion ProtocolVersion { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);

    // Messaging
    Task SendMessageAsync(string channelId, string messageId, byte[] content, CancellationToken cancellationToken);
    Task EditMessageAsync(string channelId, string messageId, byte[] content, CancellationToken cancellationToken);
    Task DeleteMessageAsync(string channelId, string messageId, CancellationToken cancellationToken);
    Task AddReactionAsync(string channelId, string messageId, string reaction, CancellationToken cancellationToken);
    Task RemoveReactionAsync(string channelId, string messageId, string reaction, CancellationToken cancellationToken);

    // Guild
    Task JoinChannelAsync(string channelId, CancellationToken cancellationToken);
    Task LeaveChannelAsync(string channelId, CancellationToken cancellationToken);
    Task AcceptGuildInviteAsync(string guildId, string inviteCode, CancellationToken cancellationToken);
    Task RevokeGuildInviteAsync(string guildId, string inviteCode, CancellationToken cancellationToken);
    Task BanGuildMemberAsync(string guildId, string userId, CancellationToken cancellationToken);

    // Social
    Task SendFriendRequestAsync(string targetUserId, CancellationToken cancellationToken);
    Task AcceptFriendRequestAsync(string sourceUserId, CancellationToken cancellationToken);
    Task RejectFriendRequestAsync(string sourceUserId, CancellationToken cancellationToken);
    Task RemoveFriendAsync(string targetUserId, CancellationToken cancellationToken);

    // Conversation
    Task CreateConversationAsync(string conversationId, IEnumerable<string> memberIds, CancellationToken cancellationToken);
    Task EditConversationAsync(string conversationId, CancellationToken cancellationToken);
    Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken);
    Task AddConversationMemberAsync(string conversationId, string userId, CancellationToken cancellationToken);
    Task RemoveConversationMemberAsync(string conversationId, string userId, CancellationToken cancellationToken);

    // Inbound
    Task HandleInboundEventAsync(FederationEvent @event, CancellationToken cancellationToken = default);
    Task<object> GetUserProfileAsync(string userId, CancellationToken cancellationToken);

    event Action<FederationEvent>? OnFederatedEventReceived;
}
