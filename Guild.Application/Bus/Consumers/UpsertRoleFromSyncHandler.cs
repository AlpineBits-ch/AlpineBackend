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
            // Same reasoning as the first-import path in ImportGuildStructureHandler, and the reason
            // this stopped being a raw assignment: a re-sync arriving after the import would
            // otherwise strip the Echo-native baseline that import had just restored, so every
            // GUILD_ROLE_UPDATE on the source guild's @everyone quietly took the wiki and own-content
            // permissions away again. Discord has no module bits, hence None.
            everyoneRole.ApplyExternalEveryonePermissions((Permissions)command.Permissions, ModulePermissions.None);
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
                Hoist = command.Hoist,
                Mentionable = command.Mentionable,
                IconUrl = command.IconUrl,
                UnicodeEmoji = command.UnicodeEmoji,
            });
            role.Position = command.Position;
            ApplyIntegrationOwnership(role, command);
            ctx.Roles.Add(role);
        }
        else
        {
            role = await ctx.Roles.FirstAsync(r => r.Id == command.EchoRoleId && r.GuildId == command.GuildId);
            role.Rename(command.Name);
            role.Color = command.Color;
            role.Position = command.Position;
            role.Permissions = (Permissions)command.Permissions;
            role.Hoist = command.Hoist;
            role.Mentionable = command.Mentionable;
            role.SetBadge(command.IconUrl, command.UnicodeEmoji);
            ApplyIntegrationOwnership(role, command);
        }

        auditLog.Log(command.GuildId, "discord-sync", AuditActionType.GuildSyncedFromDiscord, role.Id,
            new { Entity = "Role", command.Name });

        return new UpsertRoleFromSyncResponse { EchoId = role.Id };
    }

    /// <summary>Mirrors Discord's <c>managed</c> flag and role tags onto the Echo role.</summary>
    private static void ApplyIntegrationOwnership(RoleAggregate role, UpsertRoleFromSyncCommand command)
    {
        role.IsManaged = command.IsManaged;
        role.BotUserId = command.BotUserId;
        role.IntegrationId = command.IntegrationId;
    }
}
