using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class GetChannelFollowersHandler
{
    public static async Task<GetChannelFollowersResponse> Handle(GetChannelFollowersRequest request, MicroserviceContext ctx)
    {
        var channelType = await ctx.Channels.AsNoTracking()
            .Where(c => c.Id == request.ChannelId)
            .Select(c => (ChannelType?)c.Type)
            .FirstOrDefaultAsync();

        if (channelType != ChannelType.Announcement) return new GetChannelFollowersResponse { IsAnnouncementChannel = false };

        var targets = await ctx.Set<GuildChannelFollow>().AsNoTracking()
            .Where(f => f.SourceChannelId == request.ChannelId)
            .Select(f => f.TargetChannelId)
            .ToListAsync();

        return new GetChannelFollowersResponse { IsAnnouncementChannel = true, TargetChannelIds = targets };
    }
}
