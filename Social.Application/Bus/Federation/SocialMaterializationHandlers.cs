using Echo.Realtime;
using Federation.Contracts.Materialization.Social;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Api.Dtos.Realtime;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Bus.Federation;

/// <summary>Materializes remote friend-request-lifecycle federation events.</summary>
public class SocialMaterializationHandlers
{
    public static async Task Handle(FederatedFriendRequestReceived message, MicroserviceContext db,
        IHubContext<EchoRealtimeHub> hub, CancellationToken ct)
    {
        var remoteProfile = await GetOrCreateShadowProfileAsync(db, message.SenderId, message.OriginInstanceId, ct);
        var localProfile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == message.TargetUserId, ct);
        if (localProfile is null) return;

        var alreadyExists = await db.Relationships.AnyAsync(
            r => r.OwnerId == localProfile.Id && r.TargetId == remoteProfile.Id, ct);
        if (alreadyExists) return;

        var relationships = Relationship.Create(new CreateRelationshipParams
        {
            Initiator = remoteProfile.Id,
            Subject = localProfile.Id,
        });

        // Relationship.Create() makes the initiator's own copy PendingOutgoing and the subject's
        // PendingIncoming - correct here since the remote party is the initiator.
        var incoming = relationships.First(r => r.OwnerId == localProfile.Id);
        db.Relationships.Add(incoming);
        await db.SaveChangesAsync(ct);

        await PushAsync(hub, incoming, localProfile, remoteProfile, "social.FriendRequestCreated");
    }

    public static Task Handle(FederatedFriendAcceptedReceived message, MicroserviceContext db,
        IHubContext<EchoRealtimeHub> hub, CancellationToken ct)
        => ApplyRemoteTransitionAsync(db, hub, message.SenderId, RelationshipStatus.Friends,
            "social.FriendRequestAccepted", ct);

    public static Task Handle(FederatedFriendRejectedReceived message, MicroserviceContext db,
        IHubContext<EchoRealtimeHub> hub, CancellationToken ct)
        => ApplyRemoteTransitionAsync(db, hub, message.SenderId, RelationshipStatus.None,
            "social.FriendRequestRejected", ct);

    public static Task Handle(FederatedFriendRemovedReceived message, MicroserviceContext db,
        IHubContext<EchoRealtimeHub> hub, CancellationToken ct)
        => ApplyRemoteTransitionAsync(db, hub, message.SenderId, RelationshipStatus.None,
            "social.FriendRemoved", ct);

    /// <summary>
    /// Applies a remote-driven status change to the local user's own relationship row and pushes
    /// the matching social.* event.
    /// </summary>
    private static async Task ApplyRemoteTransitionAsync(
        MicroserviceContext db, IHubContext<EchoRealtimeHub> hub, string senderId,
        RelationshipStatus status, string eventName, CancellationToken ct)
    {
        var found = await FindLocalSideAsync(db, senderId, ct);
        if (found is null) return;

        var (relationship, remoteProfile) = found.Value;
        if (relationship.Status == status) return;
        if (!IsLegalRemoteTransition(relationship.Status, status)) return;

        relationship.Status = status;
        await db.SaveChangesAsync(ct);

        await PushAsync(hub, relationship, relationship.Owner, remoteProfile, eventName);
    }

    /// <summary>Which status changes a remote instance is allowed to drive on a local row.</summary>
    private static bool IsLegalRemoteTransition(RelationshipStatus current, RelationshipStatus target) =>
        target switch
        {
            // The remote accepted a request this user sent.
            RelationshipStatus.Friends => current == RelationshipStatus.PendingOutgoing,

            // The remote rejected a pending request or unfriended.
            RelationshipStatus.None => current is RelationshipStatus.PendingOutgoing
                or RelationshipStatus.PendingIncoming
                or RelationshipStatus.Friends,

            _ => false,
        };

    private static Task PushAsync(
        IHubContext<EchoRealtimeHub> hub, Relationship relationship, Profile localProfile,
        Profile remoteProfile, string eventName)
        => hub.Clients.User(localProfile.UserId).SendAsync(eventName, new FriendRelationshipPayload
        {
            RelationshipId = relationship.Id,
            Status = relationship.Status,
            UserId = remoteProfile.UserId,
            ProfileId = remoteProfile.Id,
            UserName = remoteProfile.UserName,
        });

    private static async Task<Profile> GetOrCreateShadowProfileAsync(
        MicroserviceContext db, string federatedUserId, string originInstanceId, CancellationToken ct)
    {
        var existing = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == federatedUserId, ct);
        if (existing is not null) return existing;

        // No display name is available in-band on these federation events - same limitation as
        // GuildMaterializationHandlers, same fix path (IFederationProvider.GetUserProfileAsync).
        var profile = Profile.Create(new CreateProfileParams
        {
            UserId = federatedUserId,
            Username = federatedUserId,
        });
        profile.FederatedServerId = originInstanceId;

        db.Profiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    private static async Task<(Relationship Relationship, Profile RemoteProfile)?> FindLocalSideAsync(
        MicroserviceContext db, string remoteFederatedId, CancellationToken ct)
    {
        var remoteProfile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == remoteFederatedId, ct);
        if (remoteProfile is null) return null;

        // The local side is the row owned by the local user, i.e. the one merely targeting the
        // remote shadow profile.
        var relationship = await db.Relationships
            .Include(r => r.Owner)
            .FirstOrDefaultAsync(r => r.TargetId == remoteProfile.Id, ct);

        return relationship is null ? null : (relationship, remoteProfile);
    }
}
