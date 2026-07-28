using Federation.Application.Providers;
using Federation.Application.Services;
using Messaging.Domain.Events.Conversation;

namespace Federation.Application.Bus.Outbound;

/// <summary>
/// Subscribes directly to Messaging.Domain's own conversation events - Messaging disables
/// conventional local routing (see Messaging.cs/ConfigureWolverine), so these already travel
/// over the real broker even for Messaging's own in-service handlers, meaning any other service
/// can subscribe by type with no extra publish point needed.
///
/// Like friendships, DM conversations have no admin-managed federation link: whether a given
/// conversation/member is cross-instance is determined purely by whether the relevant user id is
/// already in federated (&lt;id&gt;:&lt;domain&gt;) form - same check
/// VentaFederationProvider.CreateConversationAsync already applies to MemberIds.
///
/// No handler for a "conversation edited/renamed" event: Messaging.Application has no
/// edit/rename endpoint for conversations at all yet, so there's nothing to subscribe to -
/// venta/v0.1's conversationEdited stays outbound-unreachable until that exists.
///
/// SenderId gap: none of these domain events carry a distinct "acting user" id (edit/delete
/// carry only ConversationId; member-added/left carry the affected member, not who added/removed
/// them). ConversationCreated uses the first local member as a best-effort "creator"
/// approximation; the others send an empty SenderId rather than a fabricated one - a real, known
/// gap in the source events, not something to paper over here.
/// </summary>
public class ConversationOutboundHandlers
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
