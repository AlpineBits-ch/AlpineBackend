using Domain;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Resolves dates of birth for the <c>birthday</c> profile field (privacy spec T2-17).
///
/// <para><b>Two gates, and they are not the same gate.</b> The per-viewer one - "may <i>this</i>
/// reader see it" - is Social's, in <c>ProfileProjectionService</c>, because only Social knows the
/// viewer's relation to the subject. What this handler applies is the viewer-<i>independent</i>
/// floor: an account whose <c>BirthdayVisibility</c> is <c>Visibility.Nobody</c> has said no reader
/// may ever see it, so there is no viewer for whom answering could be correct and the date never
/// leaves this service. That floor is strictly weaker than Social's gate, so the two can never
/// disagree about which was authoritative - it just means the single most re-identifying field on
/// the account cannot be pulled over the bus by a future caller who forgets to gate it.</para>
///
/// <para><c>Nobody</c> is the shipped default (§1.1), so the default answer here is "nothing", and a
/// missing settings row produces the same. Fail-closed all the way down.</para>
///
/// <para>Every "no" looks identical - see <see cref="UserBirthdaySummary"/>. Purged accounts and bot
/// accounts both leave <c>default(DateOnly)</c> behind, which is reported as null rather than as
/// 1/1/0001.</para>
///
/// <para>Projection-only and untracked: this runs on the profile-read path and has no business
/// putting user rows into a change tracker that Wolverine's middleware commits on the way out.</para>
/// </summary>
public class GetUserBirthdaysHandler
{
    public static async Task<GetUserBirthdaysResponse> Handle(
        GetUserBirthdaysRequest request,
        MicroserviceContext ctx)
    {
        var userIds = request.UserIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? [];
        if (userIds.Count == 0) return new GetUserBirthdaysResponse();

        var rows = await ctx.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new
            {
                u.Id,
                u.BirthDate,
                VerifiedBirthDate = u.AgeVerification.BirthDate,
                // Explicitly null-checked rather than dereferenced: the navigation is optional, and
                // a left join that produced the default enum value would read as Everyone (0) for a
                // row that has no settings at all - the worst possible way to be wrong here.
                Visibility = u.UserPrivacySettings == null
                    ? (Visibility?)null
                    : u.UserPrivacySettings.BirthdayVisibility,
            })
            .ToListAsync();

        var byId = rows.ToDictionary(r => r.Id);

        var birthdays = userIds.Select(id =>
        {
            if (!byId.TryGetValue(id, out var row) || row.Visibility != Visibility.Everyone && row.Visibility != Visibility.Friends)
                return new UserBirthdaySummary { UserId = id, BirthDate = null };

            // AgeVerification.BirthDate is the authoritative copy - it is what the minor floors are
            // computed from - with the flat column as the fallback for any row written before the
            // value object existed. Both are cleared by Tombstone(), so a purged account answers null.
            var date = row.VerifiedBirthDate != default ? row.VerifiedBirthDate
                : row.BirthDate != default ? row.BirthDate
                : (DateOnly?)null;

            return new UserBirthdaySummary { UserId = id, BirthDate = date };
        }).ToList();

        return new GetUserBirthdaysResponse { Birthdays = birthdays };
    }
}
