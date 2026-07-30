using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class GetGuildEmojiHandler
{
    public static async Task<GetGuildEmojiResponse> Handle(GetGuildEmojiRequest request, MicroserviceContext ctx)
    {
        var guildId = await ctx.Channels.Where(c => c.Id == request.ChannelId).Select(c => c.GuildId).FirstOrDefaultAsync();
        if (guildId is null) return new GetGuildEmojiResponse { Found = false };

        var emoji = await ctx.Set<GuildEmoji>()
            .Where(e => e.Id == request.EmojiId && e.GuildId == guildId)
            .Select(e => new { e.Name, e.Animated })
            .FirstOrDefaultAsync();

        if (emoji is null) return new GetGuildEmojiResponse { Found = false };

        return new GetGuildEmojiResponse { Found = true, Name = emoji.Name, Animated = emoji.Animated };
    }
}
