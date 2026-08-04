using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.Relationship.Consumers;

/// <summary>Serves the block graph to the rest of the system (privacy spec T0-3).</summary>
public class GetBlockRelationshipsHandler
{
    public static async Task<GetBlockRelationshipsResponse> Handle(
        GetBlockRelationshipsRequest request, MicroserviceContext ctx)
    {
        var userIds = (request.UserIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        if (userIds.Count == 0) return new GetBlockRelationshipsResponse();

        var blocks = await ctx.Relationships.AsNoTracking()
            .Where(r => r.Status == RelationshipStatus.Blocked &&
                        (userIds.Contains(r.Owner.UserId) || userIds.Contains(r.Target.UserId)))
            .Select(r => new BlockRelationship
            {
                BlockerId = r.Owner.UserId,
                BlockedId = r.Target.UserId,
            })
            .ToListAsync();

        return new GetBlockRelationshipsResponse { Blocks = blocks };
    }
}
