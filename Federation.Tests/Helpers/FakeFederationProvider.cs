using Federation.Application.Dtos.Events;
using Federation.Application.Providers;

namespace Federation.Tests.Helpers;

/// <summary>
/// Hand-rolled no-op IFederationProvider for outbound-handler tests - mirrors this repo's
/// no-mocking-framework convention (see Guild.Tests/Helpers/FakeMessageBus.cs).
/// </summary>
public class FakeFederationProvider : IFederationProvider
{
    public record Call(string Method, params string?[] Args);

    public List<Call> Calls { get; } = new();

    public FederationProtocolVersion ProtocolVersion { get; } = new("venta", 0, 1);

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendMessageAsync(string channelId, string messageId, byte[] content, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(SendMessageAsync), channelId, messageId, senderId));
        return Task.CompletedTask;
    }

    public Task EditMessageAsync(string channelId, string messageId, byte[] content, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(EditMessageAsync), channelId, messageId, senderId));
        return Task.CompletedTask;
    }

    public Task DeleteMessageAsync(string channelId, string messageId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(DeleteMessageAsync), channelId, messageId, senderId));
        return Task.CompletedTask;
    }

    public Task AddReactionAsync(string channelId, string messageId, string reaction, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(AddReactionAsync), channelId, messageId, reaction, senderId));
        return Task.CompletedTask;
    }

    public Task RemoveReactionAsync(string channelId, string messageId, string reaction, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(RemoveReactionAsync), channelId, messageId, reaction, senderId));
        return Task.CompletedTask;
    }

    public Task JoinChannelAsync(string channelId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(JoinChannelAsync), channelId, senderId));
        return Task.CompletedTask;
    }

    public Task LeaveChannelAsync(string channelId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(LeaveChannelAsync), channelId, senderId));
        return Task.CompletedTask;
    }

    public Task AcceptGuildInviteAsync(string guildId, string inviteCode, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(AcceptGuildInviteAsync), guildId, inviteCode, senderId));
        return Task.CompletedTask;
    }

    public Task RevokeGuildInviteAsync(string guildId, string inviteCode, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(RevokeGuildInviteAsync), guildId, inviteCode, senderId));
        return Task.CompletedTask;
    }

    public Task BanGuildMemberAsync(string guildId, string userId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(BanGuildMemberAsync), guildId, userId, senderId));
        return Task.CompletedTask;
    }

    public Task SendFriendRequestAsync(string targetUserId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(SendFriendRequestAsync), targetUserId, senderId));
        return Task.CompletedTask;
    }

    public Task AcceptFriendRequestAsync(string sourceUserId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(AcceptFriendRequestAsync), sourceUserId, senderId));
        return Task.CompletedTask;
    }

    public Task RejectFriendRequestAsync(string sourceUserId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(RejectFriendRequestAsync), sourceUserId, senderId));
        return Task.CompletedTask;
    }

    public Task RemoveFriendAsync(string targetUserId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(RemoveFriendAsync), targetUserId, senderId));
        return Task.CompletedTask;
    }

    public Task CreateConversationAsync(string conversationId, IEnumerable<string> memberIds, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(CreateConversationAsync), conversationId, string.Join(",", memberIds), senderId));
        return Task.CompletedTask;
    }

    public Task EditConversationAsync(string conversationId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(EditConversationAsync), conversationId, senderId));
        return Task.CompletedTask;
    }

    public Task DeleteConversationAsync(string conversationId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(DeleteConversationAsync), conversationId, senderId));
        return Task.CompletedTask;
    }

    public Task AddConversationMemberAsync(string conversationId, string userId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(AddConversationMemberAsync), conversationId, userId, senderId));
        return Task.CompletedTask;
    }

    public Task RemoveConversationMemberAsync(string conversationId, string userId, string senderId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(nameof(RemoveConversationMemberAsync), conversationId, userId, senderId));
        return Task.CompletedTask;
    }

    public Task HandleInboundEventAsync(FederationEvent @event, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<object> GetUserProfileAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<object>(new { });

    public Task RetryUndeliveredEventsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public event Action<FederationEvent>? OnFederatedEventReceived;
}
