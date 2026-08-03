using Echo.Realtime;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Wolverine;

namespace Guild.Application.Bus.Events.Messages;

/// <summary>Mirrors MessageUpdatedForChannelHandler's channel->guild resolution - broadcasts
/// guild.MessagePinned to guild members and records an audit log entry, since pinning is gated
/// by Permissions.PinMessages and other moderators should be able to see who pinned what.</summary>
public class MessagePinnedForChannelHandler
{
    private string GetChannelKey(string channelId) => $"channel:{channelId}:guild";

    public async Task Handle(MessagePinnedForChannel message, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, AuditLogService auditLog, ILogger<MessagePinnedForChannelHandler> logger,
        ChannelAudienceService audience)
    {
        var channelKey = GetChannelKey(message.ChannelId);
        var guildId = await cache.GetStringAsync(channelKey);

        if (string.IsNullOrWhiteSpace(guildId))
        {
            guildId = await context.Channels.Where(c => c.Id == message.ChannelId).Select(c => c.GuildId).FirstOrDefaultAsync();
            if (guildId is null)
            {
                logger.LogWarning($"Channel with ID {message.ChannelId} not found in context");
                return;
            }
            await cache.SetStringAsync(channelKey, guildId);
        }

        // Channel-scoped audience - see ChannelAudienceService.
        var presence = await service.GetGuildPresenceAsync(guildId);
        var viewerIds = await audience.FilterToViewersAsync(message.ChannelId, presence.Select(p => p.UserId));
        await hub.Clients.Users(viewerIds).SendAsync("guild.MessagePinned", message);

        auditLog.Log(guildId, message.PinnedById, AuditActionType.MessagePinned, message.MessageId,
            new { message.ChannelId });
    }
}

public class MessageUnpinnedForChannelHandler
{
    private string GetChannelKey(string channelId) => $"channel:{channelId}:guild";

    public async Task Handle(MessageUnpinnedForChannel message, IHubContext<EchoRealtimeHub> hub, GuildHydrateService service,
        MicroserviceContext context, IDistributedCache cache, AuditLogService auditLog, ILogger<MessageUnpinnedForChannelHandler> logger,
        ChannelAudienceService audience)
    {
        var channelKey = GetChannelKey(message.ChannelId);
        var guildId = await cache.GetStringAsync(channelKey);

        if (string.IsNullOrWhiteSpace(guildId))
        {
            guildId = await context.Channels.Where(c => c.Id == message.ChannelId).Select(c => c.GuildId).FirstOrDefaultAsync();
            if (guildId is null)
            {
                logger.LogWarning($"Channel with ID {message.ChannelId} not found in context");
                return;
            }
            await cache.SetStringAsync(channelKey, guildId);
        }

        // Channel-scoped audience - see ChannelAudienceService.
        var presence = await service.GetGuildPresenceAsync(guildId);
        var viewerIds = await audience.FilterToViewersAsync(message.ChannelId, presence.Select(p => p.UserId));
        await hub.Clients.Users(viewerIds).SendAsync("guild.MessageUnpinned", message);

        auditLog.Log(guildId, message.UnpinnedById, AuditActionType.MessageUnpinned, message.MessageId,
            new { message.ChannelId });
    }
}
