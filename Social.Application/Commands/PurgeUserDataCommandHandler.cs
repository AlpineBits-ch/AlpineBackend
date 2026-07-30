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
        }

        var relationships = await ctx.Relationships
            .Where(r => r.OwnerId == command.UserId || r.TargetId == command.UserId)
            .ToListAsync();
        ctx.Relationships.RemoveRange(relationships);

        return new PurgeUserDataCommandResponse
        {
            UserId = command.UserId,
            Service = "social",
        };
    }
}
