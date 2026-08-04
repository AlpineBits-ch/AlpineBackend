using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.Relationship.Consumers;

/// <summary>
/// Serves the block graph to the rest of the system (privacy spec T0-3). Social is the only owner of
/// this fact, and a block that Messaging, Guild or Federation cannot see is theatre - so this handler
/// is the load-bearing half of the feature, not an accessory to the REST endpoints.
///
/// <para>Returns rows where <i>either</i> side appears in <c>UserIds</c>, so one round trip answers
/// both "did A block B" and "did B block A" for a whole conversation's membership. Callers must key
/// off the direction: a blocker may still be told about the person they blocked, while the blocked
/// party may not be told anything.</para>
///
/// <para>Deliberately uncached here. The Redis cache belongs in the *consuming* service (same shape
/// as <c>PrivacySettingsCache</c>), because Social answers from the table that defines the answer and
/// a cache in front of it would only add a staleness window to the one operation - "stop this person
/// reaching me, now" - where staleness is exactly what the user is trying to prevent.</para>
/// </summary>
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
