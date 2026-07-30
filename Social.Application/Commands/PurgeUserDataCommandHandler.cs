using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Commands;

/// <summary>Social's participant in the AccountDeletionSaga fan-out.</summary>
public class PurgeUserDataCommandHandler
{
    public static async Task<PurgeUserDataCommandResponse> Handle(PurgeUserDataCommand command, MicroserviceContext ctx)
    {
        var profile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == command.UserId);
        if (profile is not null)
        {
            var suffix = profile.UserId.Length >= 6 ? profile.UserId[^6..] : profile.UserId;
            profile.UserName = $"Deleted User {suffix}";
            profile.Bio = null;
            profile.AccentColor = null;
            profile.Font = ProfileFont.Default;

            // Relationship.OwnerId/TargetId are Profile ids, not Identity user ids (see
            // Relationship.Create's CreateRelationshipParams.Initiator/Subject, both populated from
            // Profile.Id in FriendshipEndpoints).
            var relationships = await ctx.Relationships
                .Where(r => r.OwnerId == profile.Id || r.TargetId == profile.Id)
                .ToListAsync();

            // Each friendship is two rows that reference each other via RelatedId (a self-FK - see
            // FriendshipEndpoints.CreateAsync's cross-linking comment).
            foreach (var relationship in relationships)
            {
                relationship.RelatedId = null;
            }
            await ctx.SaveChangesAsync();

            ctx.Relationships.RemoveRange(relationships);
        }

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "social",
        };
    }
}
