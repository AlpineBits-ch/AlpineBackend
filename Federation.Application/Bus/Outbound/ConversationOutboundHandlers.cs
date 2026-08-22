using Federation.Application.Providers;
using Federation.Application.Services;
using Messaging.Domain.Events.Conversation;

namespace Federation.Application.Bus.Outbound;

/// <summary>
/// Subscribes directly to Messaging.Domain's own conversation events - Messaging disables
/// conventional local routing (see Messaging.cs/ConfigureWolverine), so these already travel over
/// the real broker even for Messaging's own in-service handlers, meaning any other service can
/// subscribe by type with no extra publish point needed.
/// </summary>
public class ConversationOutboundHandler
{
    public static Task Handle(ConversationCreated message, IFederationProvider provider, UserService userService, CancellationToken ct)
    {
        var federatedMembers = message.MemberIds.Where(IsFederated).ToArray();
        if (federatedMembers.Length == 0) return Task.CompletedTask;

        var creator = message.MemberIds.FirstOrDefault(id => !IsFederated(id));
        var senderId = creator is not null ? userService.GetFederatedUserId(creator) : string.Empty;

        return provider.CreateConversationAsync(message.ConversationId, federatedMembers, senderId, ct);
    }

    public static Task Handle(ConversationMemberAdded message, IFederationProvider provider, CancellationToken ct) =>
        IsFederated(message.UserId)
            ? provider.AddConversationMemberAsync(message.ConversationId, message.UserId, string.Empty, ct)
            : Task.CompletedTask;

    public static Task Handle(ConversationMemberRemoved message, IFederationProvider provider, CancellationToken ct) =>
        IsFederated(message.UserId)
            ? provider.RemoveConversationMemberAsync(message.ConversationId, message.UserId, string.Empty, ct)
            : Task.CompletedTask;

    public static Task Handle(ConversationDeleted message, IFederationProvider provider, CancellationToken ct) =>
        provider.DeleteConversationAsync(message.ConversationId, string.Empty, ct);

    private static bool IsFederated(string id) => id.Contains(':');
}
