using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Bus.Events.Messages;

public class AutoModTriggeredHandler
{
    public async Task Handle(AutoModTriggeredEvent triggered, MicroserviceContext context, IDistributedCache cache,
        AuditLogService auditLog, ILogger<AutoModTriggeredHandler> logger)
    {
        var channelKey = $"channel:{triggered.ChannelId}:guild";
        var guildId = await cache.GetStringAsync(channelKey);

        if (string.IsNullOrWhiteSpace(guildId))
        {
            guildId = await context.Channels.Where(c => c.Id == triggered.ChannelId).Select(c => c.GuildId).FirstOrDefaultAsync();
            if (guildId is null)
            {
                logger.LogWarning("Channel with ID {ChannelId} not found in context", triggered.ChannelId);
                return;
            }
            await cache.SetStringAsync(channelKey, guildId);
        }

        auditLog.Log(guildId, triggered.UserId, AuditActionType.AutoModMessageBlocked, triggered.ChannelId,
            new { triggered.Reason });
    }
}
