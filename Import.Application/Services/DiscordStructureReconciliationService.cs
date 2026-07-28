using Import.Application.Discord;
using Import.Application.Gateway;
using Import.Application.Mapping;
using Import.Domain.Enums;
using Import.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Import.Application.Services;

/// <summary>
/// Backstop for the live Gateway sync: re-fetches each Active GuildLink's structure via REST and
/// re-applies it through the same upsert path the Gateway dispatches use, plus deletes any mapped
/// entity no longer present on the Discord side.
/// </summary>
public class DiscordStructureReconciliationService(
    IServiceScopeFactory scopeFactory, ILogger<DiscordStructureReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                await ReconcileAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Discord structure reconciliation pass failed");
            }
        }
    }

    private async Task ReconcileAllAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var links = await ctx.GuildLinks.Where(l => l.Status == GuildLinkStatus.Active).ToListAsync(ct);
        foreach (var link in links)
        {
            try
            {
                await ReconcileGuildAsync(scope.ServiceProvider, link.DiscordGuildId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reconcile Discord guild {DiscordGuildId}", link.DiscordGuildId);
            }
        }
    }

    private async Task ReconcileGuildAsync(IServiceProvider services, string discordGuildId, CancellationToken ct)
    {
        var discordApi = services.GetRequiredService<DiscordApiClient>();
        var syncHandler = services.GetRequiredService<DiscordStructureSyncHandler>();
        var ctx = services.GetRequiredService<MicroserviceContext>();

        var guild = await discordApi.GetGuildAsync(discordGuildId, ct);
        if (guild is null) return; // bot no longer in the guild - nothing to reconcile

        var roles = await discordApi.GetGuildRolesAsync(discordGuildId, ct);
        var channels = await discordApi.GetGuildChannelsAsync(discordGuildId, ct);

        foreach (var role in roles)
        {
            await syncHandler.HandleRoleUpsertAsync(discordGuildId, role, ct);
        }

        foreach (var channel in channels)
        {
            await syncHandler.HandleChannelUpsertAsync(channel, ct);
        }

        var link = await ctx.GuildLinks.FirstAsync(l => l.DiscordGuildId == discordGuildId, ct);
        var liveDiscordIds = new HashSet<string>(roles.Select(r => r.Id).Concat(channels.Select(c => c.Id)));

        var staleMappings = await ctx.ImportEntityMappings
            .Where(m => m.GuildLinkId == link.Id && !liveDiscordIds.Contains(m.DiscordId))
            .ToListAsync(ct);

        foreach (var stale in staleMappings)
        {
            if (stale.EntityType == ImportEntityType.Role)
            {
                await syncHandler.HandleRoleDeleteAsync(discordGuildId, stale.DiscordId, ct);
            }
            else
            {
                await syncHandler.HandleChannelDeleteAsync(
                    new DiscordChannelPayload
                    {
                        Id = stale.DiscordId,
                        GuildId = discordGuildId,
                        Type = stale.EntityType == ImportEntityType.Category ? DiscordChannelTypeMapper.GuildCategory : DiscordChannelTypeMapper.GuildText,
                    }, ct);
            }
        }

        link.LastSyncedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);
    }
}
