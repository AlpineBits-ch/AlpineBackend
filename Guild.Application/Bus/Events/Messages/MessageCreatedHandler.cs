using Echo.Realtime;

using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Bus.Events.Messages;

public class MessageCreatedHandler
{
    private string GetChannelKey(string channelId)
    {
        return $"channel:{channelId}:guild";
    }
    public async Task Handle(MessageCreatedForChannel message, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, IMessageBus bus, ILogger<MessageCreatedHandler> logger)
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

        // Bots.Application can't join the SignalR/Redis backplane the hub broadcast above rides
        // on, so it gets its own event - carrying the GuildId this handler just resolved, since
        // the raw MessageCreatedForChannel event doesn't have it.
        await bus.PublishAsync(new MessageCreatedForBots
        {
            GuildId = cachedGuildId,
            ChannelId = message.ChannelId,
            MessageId = message.MessageId,
            Content = message.Content,
            AuthorId = message.AuthorId,
            EncryptionState = message.EncryptionState,
            EmbedsJson = message.EmbedsJson,
            Type = message.Type,
            SystemMessageVariant = message.SystemMessageVariant,
        });

        // Union of everyone whose mention count should bump: direct @user mentions, members
        // holding an @role-mentioned role, everyone in the guild (@everyone), or everyone
        // currently present (@here) — each resolved locally since Guild already owns this data.
        var mentionedMemberIds = new HashSet<string>();

        if (message.Mentions.Count > 0)
        {
            var directMemberIds = await context.GuildMembers
                .Where(m => m.GuildId == cachedGuildId && message.Mentions.Contains(m.UserId))
                .Select(m => m.Id)
                .ToListAsync();
            mentionedMemberIds.UnionWith(directMemberIds);
        }

        if (message.RoleMentions.Count > 0)
        {
            var roleMemberIds = await context.RoleMembers
                .Where(rm => message.RoleMentions.Contains(rm.RoleId))
                .Select(rm => rm.MemberId)
                .ToListAsync();
            mentionedMemberIds.UnionWith(roleMemberIds);
        }

        if (message.MentionsEveryone)
        {
            var allMemberIds = await context.GuildMembers
                .Where(m => m.GuildId == cachedGuildId)
                .Select(m => m.Id)
                .ToListAsync();
            mentionedMemberIds.UnionWith(allMemberIds);
        }

        if (message.MentionsHere)
        {
            mentionedMemberIds.UnionWith(presence.Select(p => p.MemberId));
        }

        foreach (var memberId in mentionedMemberIds)
        {
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