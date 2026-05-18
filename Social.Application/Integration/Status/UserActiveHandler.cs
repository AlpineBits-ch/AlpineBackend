using System.Reflection.Metadata;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.Status;

public class UserActiveHandler
{
    public static async Task Handle(UserActiveEvent @event, MicroserviceContext ctx)
    {
        var profile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == @event.UserId!);
        profile?.LastSeenAt = DateTime.UtcNow;
        profile?.OnlineStatus = OnlineStatus.Online;
    }
}