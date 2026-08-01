using Domain;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Answers what protection level each requested account is on, together with the signed assertion
/// that actually proves it.
///
/// <para>A user with no row - deleted, or an id that never existed - is reported as
/// <see cref="ProtectionLevel.VerifiedDevices"/> with no assertion. Failing closed here matters:
/// the caller uses this to decide whether a device may be admitted without a human, and "we could
/// not find the account" must never read as "the permissive default applies".</para>
/// </summary>
public class GetUserProtectionLevelHandler
{
    public static async Task<GetUserProtectionLevelResponse> Handle(
        GetUserProtectionLevelRequest request,
        MicroserviceContext ctx)
    {
        var userIds = request.UserIds?.Distinct().ToList() ?? [];
        if (userIds.Count == 0) return new GetUserProtectionLevelResponse();

        var rows = await ctx.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new UserProtectionLevelResponse
            {
                UserId = u.Id,
                Level = u.ProtectionLevel,
                SignedAssertion = u.ProtectionLevelAssertion,
                Version = u.ProtectionLevelVersion,
                UpdatedAt = u.ProtectionLevelUpdatedAt,
            })
            .ToListAsync();

        var missing = userIds
            .Except(rows.Select(r => r.UserId))
            .Select(id => new UserProtectionLevelResponse
            {
                UserId = id,
                Level = ProtectionLevel.VerifiedDevices,
                Version = 0,
            });

        return new GetUserProtectionLevelResponse { Levels = rows.Concat(missing).ToList() };
    }
}
