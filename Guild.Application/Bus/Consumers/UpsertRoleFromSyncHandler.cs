using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using CreateRoleParams = Guild.Domain.Aggregates.CreateRoleParams;
using RoleAggregate = Guild.Domain.Aggregates.Role;

namespace Guild.Application.Bus.Consumers;

public class UpsertRoleFromSyncHandler
{
    public static async Task<UpsertRoleFromSyncResponse> Handle(
        UpsertRoleFromSyncCommand command, MicroserviceContext ctx, AuditLogService auditLog)
    {
        if (command.IsEveryoneRole)
        {
            var everyoneRole = await ctx.Roles.FirstAsync(r => r.GuildId == command.GuildId && r.Type == RoleType.Everyone);
            everyoneRole.Permissions = (Permissions)command.Permissions;
            everyoneRole.Color = command.Color;

            auditLog.Log(command.GuildId, "discord-sync", AuditActionType.GuildSyncedFromDiscord, everyoneRole.Id,
                new { Entity = "Role", Everyone = true });

            return new UpsertRoleFromSyncResponse { EchoId = everyoneRole.Id };
        }

        RoleAggregate role;
        if (command.EchoRoleId is null)
        {
            role = RoleAggregate.Create(new CreateRoleParams
            {
                Name = command.Name,
                Color = command.Color,
                GuildId = command.GuildId,
                Type = RoleType.None,
                Permissions = (Permissions)command.Permissions,
            });
            role.Position = command.Position;
            ctx.Roles.Add(role);
        }
        else
        {
            role = await ctx.Roles.FirstAsync(r => r.Id == command.EchoRoleId && r.GuildId == command.GuildId);
            role.Name = command.Name;
            role.Color = command.Color;
            role.Position = command.Position;
            role.Permissions = (Permissions)command.Permissions;
        }

        auditLog.Log(command.GuildId, "discord-sync", AuditActionType.GuildSyncedFromDiscord, role.Id,
            new { Entity = "Role", command.Name });

        return new UpsertRoleFromSyncResponse { EchoId = role.Id };
    }
}
