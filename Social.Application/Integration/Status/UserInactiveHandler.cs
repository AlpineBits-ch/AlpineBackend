using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Enums;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.Status;

public class UserInactiveHandler
{
    public static async Task Handle(UserInactiveEvent @event, MicroserviceContext ctx)
    {
        var profile = await ctx.Profiles.FirstOrDefaultAsync(p => p.UserId == @event.UserId!);
        profile?.LastSeenAt = DateTime.UtcNow;
        profile?.OnlineStatus = OnlineStatus.Offline;
    }
}