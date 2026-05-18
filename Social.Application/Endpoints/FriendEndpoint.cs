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
        
        var targetProfile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserName == dto.UserName && p.Hash == dto.Hash);
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
        
        first.RelatedId = null;
        
        ctx.Relationships.Add(first);
        await ctx.SaveChangesAsync();
        
        ctx.Relationships.Add(second);
        first.RelatedId = second.Id;

        var firstUserCache = $"integration_profile:user_id:{targetProfile.UserId}";
        var scondUserCache = $"integration_profile:user_id:{initiator.UserId}";

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

        friendship.Accept();

        friendship.Related.Accept();
        
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

        friendship.Reject();
        

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

        friendship.Status = RelationshipStatus.None;
        friendship.Related.Status = RelationshipStatus.None;

        return Results.Ok();
    }
}