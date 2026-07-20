using Echo.Realtime;

using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Bus.Events.Messages;

public class MessageCreatedHandler
{
    private string GetChannelKey(string channelId)
    {
        return $"channel:{channelId}:guild";
    }
    public async Task Handle(MessageCreatedForChannel message, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, ILogger<MessageCreatedHandler> logger)
    {
        var channelKey = GetChannelKey(message.ChannelId);
        var cachedGuildId = await cache.GetStringAsync(channelKey);
        string guildId;
        if (string.IsNullOrWhiteSpace(cachedGuildId))
        {
           var channel =  context.Channels.Where(c => c.Id == message.ChannelId).Select(c => c.GuildId).FirstOrDefault();
           if (channel is null)
           {
               logger.LogWarning($"Channel with ID {message.ChannelId} not found in context");
               return;
           }
           guildId = channel;
           cachedGuildId = guildId;
           await cache.SetStringAsync(channelKey, guildId);
        }

        var presence = await service.GetGuildPresenceAsync(cachedGuildId);

        await hub.Clients.Users(presence.Select(p => p.UserId).Except([message.AuthorId])).SendAsync("guild.MessageCreated", message);

        var members = await context.GuildMembers
            .Where(m => m.GuildId == cachedGuildId && message.Mentions.Contains(m.UserId))
            .ToListAsync();
        
        
        foreach (var mention in message.Mentions)
        {
            var memberId = members.FirstOrDefault(m => m.UserId == mention)?.Id;
            if(memberId is null) continue;
            var readState = await context.ReadStates
                .Where(rs => rs.ChannelId == message.ChannelId && rs.MemberId == memberId)
                .FirstOrDefaultAsync();

            if (readState is null)
            {
                readState = new ReadState()
                {
                    Id = ReadState.GenerateId(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    ChannelId = message.ChannelId,
                    MemberId = memberId,
                    LastReadMessageId = null,
                    MentionCount = 0,
                };
                context.ReadStates.Add(readState);
            }
            readState.MentionCount++;
        }
        
    }
}