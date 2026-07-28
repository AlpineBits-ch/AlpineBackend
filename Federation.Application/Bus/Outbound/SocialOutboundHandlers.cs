using Federation.Application.Providers;
using Federation.Application.Services;
using Social.Contracts.Bus.Integration.Events;

namespace Federation.Application.Bus.Outbound;

/// <summary>
/// Friendships have no guild-style admin-managed federation link (no FederatedResource lookup
/// needed): whether a given relationship is cross-instance is determined purely by whether the
/// other party's id is already in federated (&lt;id&gt;:&lt;domain&gt;) form, same check
/// VentaFederationProvider.CreateConversationAsync already uses.
///
/// NOTE: Social.Application's own "add friend" endpoint (FriendshipEndpoints.CreateAsync) only
/// looks up targets by local username today - there is currently no way for a user to actually
/// target a federated user id at all, so these handlers are correctly wired but structurally
/// unreachable until Social's add-friend flow supports specifying a remote user (e.g.
/// user@remote.domain). Flagged as a real gap, not fixed here - it's a Social.Application UX
/// change, not a Federation wiring one.
/// </summary>
public class SocialOutboundHandlers
{
    public static Task Handle(FriendRequestCreatedEvent message, IFederationProvider provider, UserService userService, CancellationToken ct) =>
        IsFederated(message.TargetUserId)
            ? provider.SendFriendRequestAsync(message.TargetUserId, userService.GetFederatedUserId(message.InitiatorUserId), ct)
            : Task.CompletedTask;

    public static Task Handle(FriendshipAcceptedEvent message, IFederationProvider provider, UserService userService, CancellationToken ct) =>
        IsFederated(message.InitiatorUserId)
            ? provider.AcceptFriendRequestAsync(message.InitiatorUserId, userService.GetFederatedUserId(message.AcceptantUserId), ct)
            : Task.CompletedTask;

    public static Task Handle(FriendRequestRejectedEvent message, IFederationProvider provider, UserService userService, CancellationToken ct) =>
        IsFederated(message.InitiatorUserId)
            ? provider.RejectFriendRequestAsync(message.InitiatorUserId, userService.GetFederatedUserId(message.TargetUserId), ct)
            : Task.CompletedTask;

    public static Task Handle(FriendRemovedEvent message, IFederationProvider provider, UserService userService, CancellationToken ct) =>
        IsFederated(message.TargetUserId)
            ? provider.RemoveFriendAsync(message.TargetUserId, userService.GetFederatedUserId(message.InitiatorUserId), ct)
            : Task.CompletedTask;

    private static bool IsFederated(string id) => id.Contains(':');
}
