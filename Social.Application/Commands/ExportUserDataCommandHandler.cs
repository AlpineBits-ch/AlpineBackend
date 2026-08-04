using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Social.Infrastructure.Persistence;

namespace Social.Api.Commands;

/// <summary>
/// Social's participant in the <c>ExportUserDataSaga</c> fan-out (T1-7) - the read-side sibling of
/// <see cref="PurgeUserDataCommandHandler"/>.
///
/// <para><b>Relationships are the one place in this service where somebody else's data is one join
/// away, and this handler does not make that join.</b> Every relationship row names two profiles: the
/// subject's and a counterparty's. The subject is entitled to know who they are friends with, when
/// the friendship was made and what state it is in - all of which is data about them. They are not
/// entitled to a copy of the other person's profile, and <c>Profile</c> here carries a username, a
/// bio and an accent colour, materialized from Identity at signup. So a counterparty appears as a
/// profile id and nothing else. The test for this handler asserts exactly that: a friend's username
/// must not appear anywhere in the fragment.</para>
///
/// <para>Blocks (<c>Status = Blocked</c>) are included in the same list, and only in the direction the
/// subject placed them. A block placed <i>against</i> the subject is the blocker's decision about
/// their own contactability, not a fact about the subject that the subject may demand - and T0-3 is
/// explicit that a block must not be visible to the person blocked. Disclosing it here would route
/// straight around that.</para>
/// </summary>
public class ExportUserDataCommandHandler
{
    public static async Task<ExportUserDataResponse> Handle(
        ExportUserDataCommand command, MicroserviceContext ctx)
    {
        var profile = await ctx.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == command.UserId);

        if (profile is null)
        {
            return new ExportUserDataResponse
            {
                ExportId = command.ExportId,
                UserId = command.UserId,
                Service = "social",
                FragmentJson = JsonSerializer.Serialize(new { profile = (object?)null, relationships = Array.Empty<object>() }, UserDataExportJson.Options),
                RowCounts = new Dictionary<string, int> { ["profile"] = 0, ["relationships"] = 0 },
            };
        }

        // Owner-side only. Relationship rows are keyed by Profile id, not by Identity user id - the
        // same distinction the purge handler had to be corrected for.
        var relationships = await ctx.Relationships
            .AsNoTracking()
            .Where(r => r.OwnerId == profile.Id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        var fragment = new
        {
            profile = new
            {
                profile.Id,
                profile.UserId,
                profile.UserName,
                profile.Bio,
                profile.AccentColor,
                font = profile.Font.ToString(),
                onlineStatus = profile.OnlineStatus.ToString(),
                profile.LastSeenAt,
                profile.FederatedServerId,
                profile.CreatedAt,
            },
            relationships = relationships.Select(r => new
            {
                r.Id,
                // The counterparty, as an opaque id. No username, no bio, no avatar - see this
                // handler's remarks.
                counterpartyProfileId = r.TargetId,
                status = r.Status.ToString(),
                r.OriginInstanceId,
                r.CreatedAt,
                r.UpdatedAt,
            }),
        };

        return new ExportUserDataResponse
        {
            ExportId = command.ExportId,
            UserId = command.UserId,
            Service = "social",
            FragmentJson = JsonSerializer.Serialize(fragment, UserDataExportJson.Options),
            RowCounts = new Dictionary<string, int>
            {
                ["profile"] = 1,
                ["relationships"] = relationships.Count,
            },
        };
    }
}
