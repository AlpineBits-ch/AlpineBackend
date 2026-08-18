using Federation.Application.Dtos.Events;

namespace Federation.Application.Providers;

public interface IFederationProvider
{
    FederationProtocolVersion ProtocolVersion { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);

    // Messaging
    Task SendMessageAsync(string channelId, string messageId, byte[] content, string senderId, FederatedAuthorDisplay author, CancellationToken cancellationToken);
    Task EditMessageAsync(string channelId, string messageId, byte[] content, string senderId, FederatedAuthorDisplay author, CancellationToken cancellationToken);
    Task DeleteMessageAsync(string channelId, string messageId, string senderId, CancellationToken cancellationToken);
    Task AddReactionAsync(string channelId, string messageId, string reaction, string senderId, CancellationToken cancellationToken);
    Task RemoveReactionAsync(string channelId, string messageId, string reaction, string senderId, CancellationToken cancellationToken);

    // Guild
    Task JoinChannelAsync(string channelId, string senderId, CancellationToken cancellationToken);
    Task LeaveChannelAsync(string channelId, string senderId, CancellationToken cancellationToken);
    Task AcceptGuildInviteAsync(string guildId, string inviteCode, string senderId, CancellationToken cancellationToken);
    Task RevokeGuildInviteAsync(string guildId, string inviteCode, string senderId, CancellationToken cancellationToken);
    Task BanGuildMemberAsync(string guildId, string userId, string senderId, CancellationToken cancellationToken);

    // Social
    Task SendFriendRequestAsync(string targetUserId, string senderId, CancellationToken cancellationToken);
    Task AcceptFriendRequestAsync(string sourceUserId, string senderId, CancellationToken cancellationToken);
    Task RejectFriendRequestAsync(string sourceUserId, string senderId, CancellationToken cancellationToken);
    Task RemoveFriendAsync(string targetUserId, string senderId, CancellationToken cancellationToken);

    // Conversation
    Task CreateConversationAsync(string conversationId, IEnumerable<string> memberIds, string senderId, CancellationToken cancellationToken);
    Task EditConversationAsync(string conversationId, string senderId, CancellationToken cancellationToken);
    Task DeleteConversationAsync(string conversationId, string senderId, CancellationToken cancellationToken);
    Task AddConversationMemberAsync(string conversationId, string userId, string senderId, CancellationToken cancellationToken);
    Task RemoveConversationMemberAsync(string conversationId, string userId, string senderId, CancellationToken cancellationToken);

    // Inbound
    Task HandleInboundEventAsync(FederationEvent @event, CancellationToken cancellationToken = default);
    Task<object> GetUserProfileAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Re-attempts delivery of outbound events that previously failed to POST - see FederationOutboundRetryService.</summary>
    Task RetryUndeliveredEventsAsync(CancellationToken cancellationToken = default);

    event Action<FederationEvent>? OnFederatedEventReceived;
}
