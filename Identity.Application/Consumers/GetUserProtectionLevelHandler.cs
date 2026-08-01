using Domain;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Answers what protection level each requested account is on, together with the signed assertion
/// that actually proves it.
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
