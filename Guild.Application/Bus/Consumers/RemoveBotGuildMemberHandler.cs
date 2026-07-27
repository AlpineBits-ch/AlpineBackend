using Echo.Realtime;
using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class RemoveBotGuildMemberHandler
{
    public static async Task Handle(
        RemoveBotGuildMemberCommand command,
        MicroserviceContext ctx,
        AuditLogService auditLog,
        IHubContext<EchoRealtimeHub> hub,
        GuildHydrateService guildHydrateService)
    {
        var member = await ctx.GuildMembers
            .FirstOrDefaultAsync(m => m.GuildId == command.GuildId && m.UserId == command.BotUserId);
        if (member is null) return;

        ctx.GuildMembers.Remove(member);

        auditLog.Log(command.GuildId, command.RemovedByUserId, AuditActionType.BotUninstalled, command.BotUserId);

        var presence = await guildHydrateService.GetGuildPresenceAsync(command.GuildId);
        var audience = presence.Select(p => p.UserId).Append(command.BotUserId).Distinct();
        await hub.Clients.Users(audience).SendAsync("guild.BotUninstalled", new { command.GuildId, UserId = command.BotUserId });
    }
}
