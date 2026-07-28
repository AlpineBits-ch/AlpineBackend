using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Bus.Consumers;

public class DeleteRoleFromSyncHandler
{
    public static async Task Handle(DeleteRoleFromSyncCommand command, MicroserviceContext ctx, AuditLogService auditLog)
    {
        var role = await ctx.Roles.FirstOrDefaultAsync(r => r.Id == command.EchoRoleId && r.GuildId == command.GuildId);
        if (role is null || role.Type == RoleType.Everyone) return;

        ctx.Roles.Remove(role);
        auditLog.Log(command.GuildId, "discord-sync", AuditActionType.GuildSyncedFromDiscord, role.Id,
            new { Entity = "Role", Deleted = true });
    }
}
