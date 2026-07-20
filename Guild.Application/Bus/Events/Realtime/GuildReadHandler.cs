using Echo.Realtime;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Events.Realtime;

public class GuildReadHandler
{
    public async Task Handle(
        UpdateGuildReadCommand message,
        MicroserviceContext microserviceContext,
        ILogger<GuildReadHandler> logger)
    {
        var channel = await microserviceContext.Channels.Select(c => new
        {
            c.Id,
            c.GuildId
        }).FirstOrDefaultAsync(c => c.Id == message.ChannelId);
        if (channel is null)
        {
            logger.LogWarning("Channel with ID {ChannelId} not found in context", message.ChannelId);
            return;
        }

        var member = await microserviceContext.GuildMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == message.UserId && m.GuildId == channel.GuildId);
        if (member is null)
        {
            logger.LogWarning("member with not found");
            return;
        }

        var lastRead =
            await microserviceContext.ReadStates.FirstOrDefaultAsync(r =>
                r.ChannelId == message.ChannelId && r.MemberId == member.Id);
        if (lastRead is null)
        {
            lastRead = new ReadState
            {
                Id = ReadState.GenerateId(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ChannelId = message.ChannelId,
                MemberId = member.Id
            };
            await microserviceContext.ReadStates.AddAsync(lastRead);
        }

        lastRead.LastReadMessageId = message.Id;
        lastRead.MentionCount = 0;
        lastRead.UpdatedAt = DateTime.UtcNow;
        await microserviceContext.SaveChangesAsync();
    }
}
