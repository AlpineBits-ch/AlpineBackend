using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>The half of the maintenance module that goes and finds people.</summary>
public class MaintenanceService(
    MicroserviceContext ctx,
    MaintenanceAlertService alerts,
    GuildPermissionService permissions,
    ILogger<MaintenanceService> logger)
{
    /// <summary>How far past a due service it is still worth a notification.</summary>
    private static readonly TimeSpan MaxServiceLateness = TimeSpan.FromDays(30);

    /// <summary>How far past the warranty warning instant it is still worth sending.</summary>
    private static readonly TimeSpan MaxWarrantyLateness =
        TimeSpan.FromDays(MaintenanceAsset.WarrantyWarningDays);

    private const int BatchSize = 200;

    /// <summary>Announces services that have come due and warranties about to lapse.</summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var services = await SweepServicesAsync(now, ct);
        var warranties = await SweepWarrantiesAsync(now, ct);

        await ctx.SaveChangesAsync(ct);

        if (services > 0 || warranties > 0)
            logger.LogDebug("Maintenance sweep handled {Services} services and {Warranties} warranties",
                services, warranties);

        return services + warranties;
    }

    private async Task<int> SweepServicesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await ctx.Set<MaintenanceAsset>()
            // Oldest first, so a backlog is stamped out of the filtered index in batches rather
            // than being re-examined every pass forever.
            .Where(a => a.ServiceNotifiedAt == null && a.NextServiceAt != null && a.NextServiceAt <= now)
            .OrderBy(a => a.NextServiceAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var quietHours = await QuietHoursAsync(candidates, ct);
        var handled = 0;

        foreach (var asset in candidates)
        {
            var fireAt = Defer(quietHours, asset.GuildId, asset.NextServiceAt!.Value);
            if (fireAt > now) continue;

            if (now - fireAt > MaxServiceLateness)
            {
                asset.ServiceNotifiedAt = now;
                handled++;
                continue;
            }

            // The module can be switched off between an asset being catalogued and its service
            // falling due, and a guild with maintenance disabled must not be told about it.
            if (!await permissions.IsFeatureEnabledAsync(asset.GuildId, GuildFeatures.Maintenance)) continue;

            await alerts.ServiceDueAsync(asset);

            // Stamped whether or not anybody was eligible to receive it.
            asset.ServiceNotifiedAt = now;
            handled++;
        }

        return handled;
    }

    private async Task<int> SweepWarrantiesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var horizon = now.AddDays(MaintenanceAsset.WarrantyWarningDays);

        var candidates = await ctx.Set<MaintenanceAsset>()
            .Where(a => a.WarrantyNotifiedAt == null && a.WarrantyUntil != null && a.WarrantyUntil <= horizon)
            .OrderBy(a => a.WarrantyUntil)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var quietHours = await QuietHoursAsync(candidates, ct);
        var handled = 0;

        foreach (var asset in candidates)
        {
            var fireAt = Defer(quietHours, asset.GuildId, asset.WarrantyWarnAt!.Value);
            if (fireAt > now) continue;

            // Everything already expired lands here, by construction: the cutoff is exactly the
            // gap between the warning instant and the expiry date.
            if (now - fireAt > MaxWarrantyLateness)
            {
                asset.WarrantyNotifiedAt = now;
                handled++;
                continue;
            }

            if (!await permissions.IsFeatureEnabledAsync(asset.GuildId, GuildFeatures.Maintenance)) continue;

            await alerts.WarrantyExpiringAsync(asset, asset.WarrantyUntil!.Value - now);

            asset.WarrantyNotifiedAt = now;
            handled++;
        }

        return handled;
    }

    private async Task<Dictionary<string, GuildQuietHoursConfig>> QuietHoursAsync(
        IReadOnlyCollection<MaintenanceAsset> assets, CancellationToken ct)
    {
        var guildIds = assets.Select(a => a.GuildId).Distinct().ToList();

        return await ctx.GuildQuietHoursConfigs.AsNoTracking()
            .Where(c => guildIds.Contains(c.GuildId))
            .ToDictionaryAsync(c => c.GuildId, ct);
    }

    /// <summary>Moves an alert instant to the end of the guild's quiet window, the way
    /// <see cref="ChoreReminderService"/> does. A deferred asset is left unstamped, so the next
    /// sweep after the window closes picks it up rather than losing the alert.</summary>
    private static DateTimeOffset Defer(
        IReadOnlyDictionary<string, GuildQuietHoursConfig> quietHours, string guildId, DateTimeOffset instant) =>
        quietHours.TryGetValue(guildId, out var config) ? config.DeferPast(instant) : instant;
}
