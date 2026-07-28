using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class DeleteChannelFromSyncHandler
{
    public static async Task Handle(DeleteChannelFromSyncCommand command, MicroserviceContext ctx, AuditLogService auditLog)
    {
        if (command.IsCategory)
        {
            var category = await ctx.Categories.FirstOrDefaultAsync(c => c.Id == command.EchoId && c.GuildId == command.GuildId);
            if (category is null) return;

            ctx.Categories.Remove(category);
            auditLog.Log(command.GuildId, "discord-sync", AuditActionType.GuildSyncedFromDiscord, category.Id,
                new { Entity = "Category", Deleted = true });
            return;
        }

        var channel = await ctx.Channels.FirstOrDefaultAsync(c => c.Id == command.EchoId && c.GuildId == command.GuildId);
        if (channel is null) return;

        ctx.Channels.Remove(channel);
        auditLog.Log(command.GuildId, "discord-sync", AuditActionType.GuildSyncedFromDiscord, channel.Id,
            new { Entity = "Channel", Deleted = true });
    }
}
