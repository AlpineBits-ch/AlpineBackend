using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class GetChannelHandler
{
    public static async Task<GetChannelResponse> Handle(GetChannelRequest request, MicroserviceContext ctx)
    {
        var channel = await ctx.Channels
            .AsNoTracking()
            .Where(c => c.Id == request.ChannelId)
            .Select(c => new ChannelInfo
            {
                Id = c.Id,
                GuildId = c.GuildId,
                Name = c.Name,
                Type = c.Type.ToString(),
                Position = c.Position,
                CategoryId = c.CategoryId,
            })
            .FirstOrDefaultAsync();

        return new GetChannelResponse { Channel = channel };
    }
}
