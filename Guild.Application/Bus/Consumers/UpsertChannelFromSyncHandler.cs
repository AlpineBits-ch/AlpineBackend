using Guild.Application.Services;
using Guild.Contracts.Bus.Commands;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using ChannelAggregate = Guild.Domain.Aggregates.Channel;
using CreateChannelParams = Guild.Domain.Aggregates.CreateChannelParams;

namespace Guild.Application.Bus.Consumers;

/// <summary>
/// Applies one Discord CHANNEL_CREATE/CHANNEL_UPDATE dispatch (relayed by Import.Application's
/// Gateway client) to an already-linked guild. Categories are just type:4 channels in Discord's
/// model, hence the IsCategory branch instead of a separate command.
/// </summary>
public class UpsertChannelFromSyncHandler
{
    public static async Task<UpsertChannelFromSyncResponse> Handle(
        UpsertChannelFromSyncCommand command,
        MicroserviceContext ctx,
        AuditLogService auditLog,
        GuildPermissionService permissionService)
    {
        if (command.IsCategory)
        {
            Category category;
            if (command.EchoCategoryId is null)
            {
                category = Category.Create(new CreateCategoryParams
                {
                    Name = command.Name,
                    GuildId = command.GuildId,
                    Position = command.Position,
                });
                ctx.Categories.Add(category);
            }
            else
            {
                category = await ctx.Categories.FirstAsync(c => c.Id == command.EchoCategoryId && c.GuildId == command.GuildId);
                category.Name = command.Name;
                category.Position = command.Position;
            }

            await ApplyOverwritesAsync(ctx, command.Overwrites, categoryId: category.Id, channelId: null);
            auditLog.Log(command.GuildId, "discord-sync", AuditActionType.GuildSyncedFromDiscord, category.Id,
                new { Entity = "Category", command.Name });

            return new UpsertChannelFromSyncResponse { EchoId = category.Id };
        }

        if (!Enum.TryParse<ChannelType>(command.Type, out var channelType))
        {
            channelType = ChannelType.Text;
        }

        ChannelAggregate channel;
        if (command.EchoChannelId is null)
        {
            channel = ChannelAggregate.Create(new CreateChannelParams
            {
                Name = command.Name,
                Description = "",
                Type = channelType,
                CategoryId = command.EchoParentCategoryId,
                GuildId = command.GuildId,
                Position = command.Position,
            });
            channel.IsAgeRestricted = command.IsAgeRestricted;
            channel.SlowModeSeconds = command.SlowModeSeconds;
            ctx.Channels.Add(channel);
        }
        else
        {
            channel = await ctx.Channels.FirstAsync(c => c.Id == command.EchoChannelId && c.GuildId == command.GuildId);
            channel.Update(new ChannelAggregate.UpdateChannelParams
            {
                Name = command.Name,
                Description = channel.Description ?? "",
                IsAgeRestricted = command.IsAgeRestricted,
                IsPrivate = channel.IsPrivate,
                SlowModeSeconds = command.SlowModeSeconds,
            });
            channel.Position = command.Position;
            channel.CategoryId = command.EchoParentCategoryId;
            channel.Type = channelType;

            // Discord is authoritative for rate_limit_per_user on a synced channel, so a sync
            // that lowers it must not leave Messaging enforcing the old window from cache.
            await permissionService.InvalidateChannelSlowModeCacheAsync(channel.Id);
        }

        await ApplyOverwritesAsync(ctx, command.Overwrites, categoryId: null, channelId: channel.Id);
        auditLog.Log(command.GuildId, "discord-sync", AuditActionType.GuildSyncedFromDiscord, channel.Id,
            new { Entity = "Channel", command.Name });

        return new UpsertChannelFromSyncResponse { EchoId = channel.Id };
    }

    private static async Task ApplyOverwritesAsync(
        MicroserviceContext ctx, List<SyncOverwriteDto> overwrites, string? categoryId, string? channelId)
    {
        var existing = await ctx.Set<ChannelPermission>()
            .Where(p => (categoryId != null && p.CategoryId == categoryId) || (channelId != null && p.ChannelId == channelId))
            .ToListAsync();
        ctx.Set<ChannelPermission>().RemoveRange(existing);

        foreach (var overwrite in overwrites)
        {
            ctx.Set<ChannelPermission>().Add(new ChannelPermission
            {
                Id = ChannelPermission.GenerateId(),
                ChannelId = channelId,
                CategoryId = categoryId,
                RoleId = overwrite.EchoRoleId,
                AllowPermissions = (Permissions)overwrite.AllowPermissions,
                DenyPermissions = (Permissions)overwrite.DenyPermissions,
            });
        }
    }
}
