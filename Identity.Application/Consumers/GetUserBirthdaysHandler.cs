using Domain;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Resolves dates of birth for the <c>birthday</c> profile field (privacy spec T2-17).
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
