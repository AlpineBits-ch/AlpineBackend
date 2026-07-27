using Echo.Realtime;
using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class CreateBotGuildMemberHandler
{
    public static async Task<CreateBotGuildMemberResponse> Handle(
        CreateBotGuildMemberCommand command,
        MicroserviceContext ctx,
        AuditLogService auditLog,
        IHubContext<EchoRealtimeHub> hub,
        GuildHydrateService guildHydrateService)
    {
        var existing = await ctx.GuildMembers
            .FirstOrDefaultAsync(m => m.GuildId == command.GuildId && m.UserId == command.BotUserId);
        if (existing is not null)
        {
            return new CreateBotGuildMemberResponse { GuildMemberId = existing.Id };
        }

        var member = GuildMember.CreateForUser(new CreateGuildMemberParams
        {
            GuildId = command.GuildId,
            UserId = command.BotUserId,
            Type = MemberType.Bot,
            Username = command.BotDisplayName,
            Hash = "0000",
        });
        member.AllowPermissions = (Permissions)command.GrantedPermissions;

        ctx.GuildMembers.Add(member);

        auditLog.Log(command.GuildId, command.InstalledByUserId, AuditActionType.BotInstalled, command.BotUserId,
            new { command.GrantedPermissions });

        var presence = await guildHydrateService.GetGuildPresenceAsync(command.GuildId);
        var audience = presence.Select(p => p.UserId).Append(command.BotUserId).Distinct();
        await hub.Clients.Users(audience).SendAsync("guild.BotInstalled", new { command.GuildId, UserId = command.BotUserId });

        return new CreateBotGuildMemberResponse { GuildMemberId = member.Id };
    }
}
