using Guild.Contracts.Bus.Commands;
using Import.Application.Discord;
using Import.Application.Mapping;
using Import.Domain.Entity;
using Import.Domain.Enums;
using Import.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Import.Application.Gateway;

/// <summary>
/// Applies one already-parsed Discord Gateway dispatch (CHANNEL_*/GUILD_ROLE_*) to whichever Echo
/// guild is linked to that Discord guild.
/// </summary>
public class DiscordStructureSyncHandler(MicroserviceContext ctx, IMessageBus bus, ILogger<DiscordStructureSyncHandler> logger)
{
    public async Task HandleChannelUpsertAsync(DiscordChannelPayload channel, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(channel.GuildId) || DiscordChannelTypeMapper.IsThread(channel.Type)) return;

        var link = await GetActiveLinkAsync(channel.GuildId, ct);
        if (link is null) return;

        var isCategory = DiscordChannelTypeMapper.IsCategory(channel.Type);
        var entityType = isCategory ? ImportEntityType.Category : ImportEntityType.Channel;
        var mapping = await GetMappingAsync(link.Id, channel.Id, entityType, ct);

        string? echoParentCategoryId = null;
        if (!isCategory && channel.ParentId is not null)
        {
            var parentMapping = await GetMappingAsync(link.Id, channel.ParentId, ImportEntityType.Category, ct);
            echoParentCategoryId = parentMapping?.EchoId;
        }

        var overwrites = new List<SyncOverwriteDto>();
        foreach (var overwrite in channel.PermissionOverwrites.Where(o => o.Type == 0))
        {
            var roleMapping = await GetMappingAsync(link.Id, overwrite.Id, ImportEntityType.Role, ct);
            if (roleMapping is null) continue;
            overwrites.Add(new SyncOverwriteDto
            {
                EchoRoleId = roleMapping.EchoId,
                AllowPermissions = DiscordPermissionMapper.ToEchoPermissions(overwrite.Allow),
                DenyPermissions = DiscordPermissionMapper.ToEchoPermissions(overwrite.Deny),
            });
        }

        var response = await bus.InvokeAsync<UpsertChannelFromSyncResponse>(new UpsertChannelFromSyncCommand
        {
            GuildId = link.EchoGuildId,
            EchoCategoryId = isCategory ? mapping?.EchoId : null,
            EchoChannelId = isCategory ? null : mapping?.EchoId,
            IsCategory = isCategory,
            Name = channel.Name ?? (isCategory ? "Category" : "channel"),
            Type = isCategory ? null : DiscordChannelTypeMapper.ToEchoChannelType(channel.Type),
            EchoParentCategoryId = echoParentCategoryId,
            Position = channel.Position,
            IsAgeRestricted = channel.Nsfw,
            SlowModeSeconds = channel.RateLimitPerUser ?? 0,
            Overwrites = overwrites,
        }, ct);

        await UpsertMappingAsync(link, channel.Id, entityType, response.EchoId, ct);
        logger.LogInformation("Synced Discord channel {DiscordChannelId} -> Echo {EchoId} for guild {EchoGuildId}",
            channel.Id, response.EchoId, link.EchoGuildId);
    }

    public async Task HandleChannelDeleteAsync(DiscordChannelPayload channel, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(channel.GuildId)) return;

        var link = await GetActiveLinkAsync(channel.GuildId, ct);
        if (link is null) return;

        var isCategory = DiscordChannelTypeMapper.IsCategory(channel.Type);
        var entityType = isCategory ? ImportEntityType.Category : ImportEntityType.Channel;
        var mapping = await GetMappingAsync(link.Id, channel.Id, entityType, ct);
        if (mapping is null) return;

        await bus.InvokeAsync(new DeleteChannelFromSyncCommand
        {
            GuildId = link.EchoGuildId,
            EchoId = mapping.EchoId,
            IsCategory = isCategory,
        }, ct);

        ctx.ImportEntityMappings.Remove(mapping);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task HandleRoleUpsertAsync(string discordGuildId, DiscordRolePayload role, CancellationToken ct)
    {
        var link = await GetActiveLinkAsync(discordGuildId, ct);
        if (link is null) return;

        var isEveryone = role.Id == discordGuildId;
        var mapping = await GetMappingAsync(link.Id, role.Id, ImportEntityType.Role, ct);

        var response = await bus.InvokeAsync<UpsertRoleFromSyncResponse>(new UpsertRoleFromSyncCommand
        {
            GuildId = link.EchoGuildId,
            EchoRoleId = mapping?.EchoId,
            Name = role.Name,
            Color = $"#{role.Color:X6}",
            Position = role.Position,
            Permissions = DiscordPermissionMapper.ToEchoPermissions(role.Permissions),
            Hoist = role.Hoist,
            Mentionable = role.Mentionable,
            IconUrl = DiscordRoleMapper.IconUrl(role),
            UnicodeEmoji = DiscordRoleMapper.UnicodeEmoji(role),
            IsManaged = role.Managed,
            BotUserId = role.Tags?.BotId,
            IntegrationId = role.Tags?.IntegrationId,
            IsEveryoneRole = isEveryone,
        }, ct);

        await UpsertMappingAsync(link, role.Id, ImportEntityType.Role, response.EchoId, ct);
    }

    public async Task HandleRoleDeleteAsync(string discordGuildId, string discordRoleId, CancellationToken ct)
    {
        var link = await GetActiveLinkAsync(discordGuildId, ct);
        if (link is null) return;

        var mapping = await GetMappingAsync(link.Id, discordRoleId, ImportEntityType.Role, ct);
        if (mapping is null) return;

        await bus.InvokeAsync(new DeleteRoleFromSyncCommand { GuildId = link.EchoGuildId, EchoRoleId = mapping.EchoId }, ct);

        ctx.ImportEntityMappings.Remove(mapping);
        await ctx.SaveChangesAsync(ct);
    }

    private async Task<GuildLink?> GetActiveLinkAsync(string discordGuildId, CancellationToken ct)
    {
        var link = await ctx.GuildLinks.FirstOrDefaultAsync(l => l.DiscordGuildId == discordGuildId, ct);
        if (link is null || link.Status != GuildLinkStatus.Active) return null;
        return link;
    }

    private Task<ImportEntityMapping?> GetMappingAsync(string guildLinkId, string discordId, ImportEntityType type, CancellationToken ct) =>
        ctx.ImportEntityMappings.FirstOrDefaultAsync(
            m => m.GuildLinkId == guildLinkId && m.DiscordId == discordId && m.EntityType == type, ct);

    private async Task UpsertMappingAsync(GuildLink link, string discordId, ImportEntityType type, string echoId, CancellationToken ct)
    {
        var mapping = await GetMappingAsync(link.Id, discordId, type, ct);
        if (mapping is null)
        {
            ctx.ImportEntityMappings.Add(new ImportEntityMapping
            {
                Id = ImportEntityMapping.GenerateId(),
                GuildLinkId = link.Id,
                DiscordId = discordId,
                EntityType = type,
                EchoId = echoId,
            });
        }
        else
        {
            mapping.EchoId = echoId;
        }

        link.LastSyncedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }
}
