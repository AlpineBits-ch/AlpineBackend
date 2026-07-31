using Echo.Realtime;

using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
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
        MicroserviceContext context, IDistributedCache cache, IMessageBus bus, ILogger<MessageCreatedHandler> logger,
        NotificationResolutionService notificationService)
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

        await TouchThreadActivityAsync(message.ChannelId, context);

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

        await PublishPushRecipientsAsync(message, cachedGuildId, presence, mentionedMemberIds, context, notificationService, bus);
    }

    /// <summary>
    /// Works out who should get a phone notification for this message and hands the list to
    /// Messaging, which owns Firebase.
    /// </summary>
    private static async Task PublishPushRecipientsAsync(
        MessageCreatedForChannel message,
        string guildId,
        IReadOnlyCollection<MemberPresenceState> presence,
        HashSet<string> mentionedMemberIds,
        MicroserviceContext context,
        NotificationResolutionService notificationService,
        IMessageBus bus)
    {
        // Everyone who could conceivably be notified.
        var candidates = await context.GuildMembers
            .AsNoTracking()
            .Where(m => m.GuildId == guildId && m.UserId != message.AuthorId)
            .Select(m => new { m.Id, m.UserId })
            .ToListAsync();

        if (candidates.Count == 0) return;

        // Connected members already received the message over the realtime hub a few lines above;
        // pushing to them as well is how a notification arrives on the phone for something the
        // user is actively looking at on their desktop.
        var connectedUserIds = presence.Select(p => p.UserId).ToHashSet();

        var resolved = await notificationService.ResolveForChannelAsync(
            message.ChannelId, candidates.Select(c => c.Id).ToList());

        var roleMentionedMemberIds = message.RoleMentions.Count > 0
            ? (await context.RoleMembers.AsNoTracking()
                .Where(rm => message.RoleMentions.Contains(rm.RoleId))
                .Select(rm => rm.MemberId)
                .ToListAsync()).ToHashSet()
            : [];

        var recipients = new List<string>();
        foreach (var candidate in candidates)
        {
            if (connectedUserIds.Contains(candidate.UserId)) continue;
            if (!resolved.TryGetValue(candidate.Id, out var settings)) continue;
            if (!settings.MobilePush) continue;

            var isDirectMention = message.Mentions.Contains(candidate.UserId);
            var isRoleMention = roleMentionedMemberIds.Contains(candidate.Id);
            var isEveryoneMention = message.MentionsEveryone || message.MentionsHere;

            if (settings.ShouldNotify(isDirectMention, isRoleMention, isEveryoneMention))
                recipients.Add(candidate.UserId);
        }

        if (recipients.Count == 0) return;

        await bus.PublishAsync(new ChannelPushRequested
        {
            GuildId = guildId,
            ChannelId = message.ChannelId,
            MessageId = message.MessageId,
            UserIds = recipients,
            AuthorId = message.AuthorId,
            Content = message.Content,
            IsEncrypted = message.EncryptionState != MessageEncryptionState.Plain,
            MlsGeneration = message.MlsGeneration,
        });
    }

    /// <summary>
    /// Denormalizes "when did this post last see activity" onto the thread's channel row.
    /// </summary>
    private static async Task TouchThreadActivityAsync(string channelId, MicroserviceContext context)
    {
        var thread = await context.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.Type == ChannelType.Thread);
        if (thread is null) return;

        var now = DateTimeOffset.UtcNow;

        thread.LastActivityAt = now;
        thread.MessageCount++;

        // Slides against the stored window, not against the previous deadline - the duration is
        // fixed for the post's lifetime, only the deadline moves.
        if (thread.AutoArchiveMinutes is > 0)
            thread.AutoArchiveAt = now.AddMinutes(thread.AutoArchiveMinutes.Value);
    }
}