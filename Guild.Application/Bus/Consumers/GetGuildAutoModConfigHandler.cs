using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class GetGuildAutoModConfigHandler
{
    public static async Task<GetGuildAutoModConfigResponse> Handle(GetGuildAutoModConfigRequest request, MicroserviceContext ctx)
    {
        var guildId = await ctx.Channels.Where(c => c.Id == request.ChannelId).Select(c => c.GuildId).FirstOrDefaultAsync();
        if (guildId is null) return new GetGuildAutoModConfigResponse();

        var config = await ctx.Set<GuildAutoModConfig>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId);
        if (config is null || !config.Enabled) return new GetGuildAutoModConfigResponse { GuildId = guildId };

        return new GetGuildAutoModConfigResponse
        {
            GuildId = guildId,
            Enabled = true,
            BlockedWords = config.BlockedWords,
            MaxMessagesPerInterval = config.MaxMessagesPerInterval,
            IntervalSeconds = config.IntervalSeconds,
        };
    }
}
