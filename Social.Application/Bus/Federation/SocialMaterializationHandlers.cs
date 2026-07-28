using Federation.Contracts.Materialization.Social;
using Microsoft.EntityFrameworkCore;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Bus.Federation;

/// <summary>
/// Materializes remote friend-request-lifecycle federation events. A remote user who has never
/// interacted with this instance before has no local Profile row at all - unlike GuildMember
/// (which hangs off an existing local Guild), a friendship needs a shadow Profile for the remote
/// party to even exist as an addressable entity, flagged via the existing (previously unused)
/// Profile.FederatedServerId field. FederatedServerId doubles as the marker Relationship rows
/// don't need their own copy of - a relationship is federated if either party's Profile is.
///
/// Idempotent by natural business key (Owner/Target profile pair), not by EventId - same
/// reasoning as GuildMaterializationHandlers.
/// </summary>
public class SocialMaterializationHandlers
{
    public static async Task Handle(FederatedFriendRequestReceived message, MicroserviceContext db, CancellationToken ct)
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
        // PendingIncoming - correct here since the remote party is the initiator. The outgoing
        // (remote-owned) side isn't meaningful to persist as its own queryable row on this
        // instance; only the local (incoming) side needs to exist locally.
        var incoming = relationships.First(r => r.OwnerId == localProfile.Id);
        db.Relationships.Add(incoming);
        await db.SaveChangesAsync(ct);
    }

    public static async Task Handle(FederatedFriendAcceptedReceived message, MicroserviceContext db, CancellationToken ct)
    {
        var relationship = await FindLocalSideAsync(db, message.SenderId, ct);
        if (relationship is null) return;

        relationship.Status = RelationshipStatus.Friends;
        await db.SaveChangesAsync(ct);
    }

    public static async Task Handle(FederatedFriendRejectedReceived message, MicroserviceContext db, CancellationToken ct)
    {
        var relationship = await FindLocalSideAsync(db, message.SenderId, ct);
        if (relationship is null) return;

        relationship.Status = RelationshipStatus.None;
        await db.SaveChangesAsync(ct);
    }

    public static async Task Handle(FederatedFriendRemovedReceived message, MicroserviceContext db, CancellationToken ct)
    {
        var relationship = await FindLocalSideAsync(db, message.SenderId, ct);
        if (relationship is null) return;

        relationship.Status = RelationshipStatus.None;
        await db.SaveChangesAsync(ct);
    }

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

    private static async Task<Relationship?> FindLocalSideAsync(
        MicroserviceContext db, string remoteFederatedId, CancellationToken ct)
    {
        var remoteProfile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == remoteFederatedId, ct);
        if (remoteProfile is null) return null;

        // The local side is whichever of the pair isn't the remote shadow profile.
        return await db.Relationships.FirstOrDefaultAsync(
            r => r.OwnerId == remoteProfile.Id || r.TargetId == remoteProfile.Id, ct);
    }
}
