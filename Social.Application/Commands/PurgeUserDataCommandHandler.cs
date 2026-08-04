using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Commands;

/// <summary>
/// Social's participant in the AccountDeletionSaga fan-out. Profile carries its own copy of
/// UserName/Bio/AccentColor/Font (materialized once from Identity by the UserRegistrationSaga
/// at signup, not re-resolved live afterwards) so, unlike Guild/Messaging's purely
/// pointer-referenced data, it needs its own anonymization step rather than relying solely on
/// Identity.ApplicationUser.Tombstone. Relationship rows are removed outright (both directions)
/// so the deleted user disappears from former friends' friend lists - matching Guild's
/// GuildMember removal, which does the same for server membership.
///
/// <para>That sweep also carries the blocks required by privacy spec T0-3 ("blocks referencing a
/// purged user are deleted"), in both directions: a block is a Relationship row with
/// <c>Status = Blocked</c>, so the OwnerId/TargetId filter below catches blocks the purged user
/// placed and blocks placed against them. Nothing extra is needed here, but the requirement is
/// load-bearing and covered by its own test - a block surviving its subject would keep a dead
/// account suppressing a live one's messages forever.</para>
/// </summary>
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
            // Relationship.Create's CreateRelationshipParams.Initiator/Subject, both populated
            // from Profile.Id in FriendshipEndpoints). Filtering directly on command.UserId here
            // never matched any row, so purged users kept showing up on former friends' lists.
            var relationships = await ctx.Relationships
                .Where(r => r.OwnerId == profile.Id || r.TargetId == profile.Id)
                .ToListAsync();

            // Each friendship is two rows that reference each other via RelatedId (a self-FK -
            // see FriendshipEndpoints.CreateAsync's cross-linking comment). Deleting both rows of
            // a pair in the same batch is a circular dependency EF's change tracker can't
            // topologically sort ("Unable to save changes because a circular dependency was
            // detected"), regardless of how many pairs are involved. Breaking the cycle requires
            // an explicit intermediate save nulling RelatedId first - the same early-flush
            // exception already used for the mirror problem (inserting a circular pair) in
            // FriendshipEndpoints.CreateAsync.
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
