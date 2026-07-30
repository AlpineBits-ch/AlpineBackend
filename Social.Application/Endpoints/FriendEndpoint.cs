using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Social.Api.Dtos.Request;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;
using Wolverine.Http;

namespace Social.Api.Endpoints;


[Authorize]
public static class FriendshipEndpoints
{
    
    [WolverinePost("/api/v1/relationships")]
    public static async Task<IResult> CreateAsync(
        CreateFriendshipDto dto, 
        [NotBody] MicroserviceContext ctx, 
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var initiator = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (initiator is null) return Results.BadRequest("Initiator profile not found.");
        
        var targetProfile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserName == dto.UserName );
        if(targetProfile is null) return Results.BadRequest("Target profile not found.");
        if (initiator.Id == targetProfile.Id) 
            return Results.BadRequest("You cannot friend yourself.");

        var existing = await ctx.Relationships.AnyAsync(r => 
            r.OwnerId == initiator.Id && r.TargetId == targetProfile.Id);
        
        if (existing) return Results.Conflict("Relationship already exists.");

        var relationships = Relationship.Create(new CreateRelationshipParams
        {
            Subject = targetProfile.Id,
            Initiator = initiator.Id 
        });



        var first = relationships.First();
        var second = relationships.Last();

        // Relationship.Create() cross-links RelatedId (first -> second, second -> first) - a
        // circular FK within the same table. Nulling first.RelatedId lets it insert on its own
        // (breaking the cycle) before second (which validly points at first's now-persisted id)
        // is added; first.RelatedId is then restored and requires its own save.
        first.RelatedId = null;

        ctx.Relationships.Add(first);
        await ctx.SaveChangesAsync();

        ctx.Relationships.Add(second);
        first.RelatedId = second.Id;

        // No second SaveChangesAsync here - WolverineHttp endpoints auto-commit the ambient
        // DbContext once the endpoint returns (opts.Policies.AutoApplyTransactions(), same as bus
        // handlers), so `second`'s insert and `first.RelatedId`'s update land in that commit.
        return Results.Ok();
    }


    [WolverinePost("/api/v1/relationships/{id}/accept")]
    public static async Task<IResult> AcceptAsync(
        string id, 
        [NotBody]MicroserviceContext ctx,
        [NotBody] IDistributedCache cache,
        ClaimsPrincipal user)
    {
        var friendship = await ctx.Relationships
            .Include(r => r.Related)
            .Include(r => r.Owner)
            .Include(r => r.Target)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (friendship == null) return Results.NotFound();

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var currentProfile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == userId);
        
        if (friendship.OwnerId != currentProfile?.Id)
            return Results.Forbid();

        // Accept() is a no-op once the pair is already Friends, so a double accept (double tap,
        // client retry, two devices) can't raise a second FriendRequestAccepted and therefore
        // can't fan out a duplicate social.FriendRequestAccepted push. Returning early keeps the
        // response idempotent too, and stops a rejected/removed (None) request being resurrected
        // into a friendship without a fresh request.
        if (!friendship.Accept())
            return friendship.Status == RelationshipStatus.Friends
                ? Results.Accepted()
                : Results.BadRequest("Relationship is not a pending friend request.");

        // Null for a federation-materialized relationship - only the local half of those is
        // persisted, so there is no mirrored row to flip.
        friendship.Related?.Accept();

        var firstUserCache = $"integration_profile:user_id:{friendship.Target.UserId}";
        var secondUserCache = $"integration_profile:user_id:{friendship.Owner.UserId}";

        await cache.RemoveAsync(firstUserCache);
        await cache.RemoveAsync(secondUserCache);

        return Results.Accepted();
    }

    [WolverinePost("/api/v1/relationships/{id}/reject")]
    public static async Task<IResult> RejectAsync(
        string id,
        [NotBody]MicroserviceContext ctx)
    {
        var friendship = await ctx.Relationships
            .Include(r => r.Related)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (friendship == null) return Results.NotFound();

        // Already cleared - stay idempotent rather than raising a second FriendRequestRejected
        // and pushing social.FriendRequestRejected twice.
        if (!friendship.Reject()) return Results.Accepted();

        // Mirrors AcceptAsync/RevokeAsync, which both update the Related side too - without this
        // the initiator's own (PendingOutgoing) row was left stuck forever, so a rejected request
        // kept showing as "pending" to the initiator even though the recipient had rejected it.
        friendship.Related?.Reject();

        return Results.Accepted();
    }

    [WolverinePost("/api/v1/relationships/{id}/revoke")]
    public static async Task<IResult> RevokeAsync(
        string id,
        [NotBody]MicroserviceContext ctx)
    {
        var friendship = await ctx.Relationships
            .Include(r => r.Related)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (friendship == null) return Results.NotFound();

        // Same idempotency guard as Accept/Reject - a repeated revoke must not re-raise
        // FriendRemoved and push social.FriendRemoved a second time.
        if (!friendship.Remove()) return Results.Ok();

        friendship.Related?.Remove();

        return Results.Ok();
    }
}