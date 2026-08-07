using Guild.Contracts;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Who gets told about the house's equipment, and when.</summary>
public class MaintenanceAlertService(
    MicroserviceContext ctx,
    HouseholdNotifier notifier,
    GuildPermissionService permissions,
    ILogger<MaintenanceAlertService> logger)
{
    /// <summary>Ceiling on how many members one alert resolves permissions for, matching
    /// <see cref="HouseholdAlertService"/>. A household is under a dozen people; this only bites on
    /// a large guild that happens to have the module on, where buzzing everyone is wrong
    /// anyway.</summary>
    private const int MaxRecipients = 200;

    /// <summary>Longest status note carried into a push body. Past this it is read in the app.</summary>
    private const int MaxNoteLength = 120;

    public const string KindMaintenanceDue = "maintenance.due";
    public const string KindMaintenanceWarranty = "maintenance.warranty";
    public const string KindMaintenanceBroken = "maintenance.broken";

    /// <summary>Tells the channel that something is due for a service.</summary>
    /// <returns>How many people were told, so the sweep can log something useful.</returns>
    public async Task<int> ServiceDueAsync(MaintenanceAsset asset) =>
        await SafelyAsync(nameof(ServiceDueAsync), async () =>
        {
            var recipients = await ViewersOfAsync(asset.GuildId, asset.ChannelId, except: null);
            if (recipients.Count == 0) return 0;

            await notifier.AlertAsync(
                asset.GuildId, asset.ChannelId, recipients,
                KindMaintenanceDue,
                AlertText.Raw(asset.Name),
                AlertText.Loc(HouseholdLocKeys.MaintenanceDueBody, "This is due for a service."),
                asset.Id,
                new { asset.NextServiceAt, asset.LastServicedAt, asset.VendorName, asset.VendorPhone });

            return recipients.Count;
        });

    /// <summary>Tells the channel a warranty is about to lapse, and how long is left.</summary>
    public async Task<int> WarrantyExpiringAsync(MaintenanceAsset asset, TimeSpan remaining) =>
        await SafelyAsync(nameof(WarrantyExpiringAsync), async () =>
        {
            var recipients = await ViewersOfAsync(asset.GuildId, asset.ChannelId, except: null);
            if (recipients.Count == 0) return 0;

            var duration = DescribeDuration(remaining);

            await notifier.AlertAsync(
                asset.GuildId, asset.ChannelId, recipients,
                KindMaintenanceWarranty,
                AlertText.Raw(asset.Name),
                AlertText.Loc(HouseholdLocKeys.MaintenanceWarrantyBody,
                    $"The warranty runs out in {duration}.", duration),
                asset.Id,
                new { asset.WarrantyUntil, asset.PurchasedAt, asset.Brand, asset.Model, asset.SerialNumber });

            return recipients.Count;
        });

    /// <summary>Tells everyone but the finder that something has broken.</summary>
    public async Task BrokenAsync(MaintenanceAsset asset, string actorUserId)
    {
        await SafelyAsync(nameof(BrokenAsync), async () =>
        {
            var recipients = await ViewersOfAsync(asset.GuildId, asset.ChannelId, except: actorUserId);
            if (recipients.Count == 0) return;

            var note = asset.StatusNote?.Trim();
            if (note is { Length: > MaxNoteLength }) note = note[..MaxNoteLength].TrimEnd() + "...";

            await notifier.AlertAsync(
                asset.GuildId, asset.ChannelId, recipients,
                KindMaintenanceBroken,
                AlertText.Raw(asset.Name),
                AlertText.Loc(HouseholdLocKeys.MaintenanceBrokenBody,
                    "Marked as broken. Don't use it for now."),
                asset.Id,
                new { asset.Location, Note = note, ReportedBy = actorUserId });
        });
    }

    /// <summary>"3 weeks", "5 days", "1 day".</summary>
    internal static string DescribeDuration(TimeSpan remaining)
    {
        var days = (int)Math.Ceiling(remaining.TotalDays);

        if (days <= 0) return "less than a day";
        if (days == 1) return "1 day";
        if (days < 14) return $"{days} days";

        var weeks = days / 7;
        return weeks == 1 ? "1 week" : $"{weeks} weeks";
    }

    /// <summary>Everyone in the guild who can see this channel, minus one.</summary>
    private async Task<List<string>> ViewersOfAsync(string guildId, string channelId, string? except)
    {
        var query = ctx.GuildMembers.AsNoTracking().Where(m => m.GuildId == guildId);
        if (except is not null) query = query.Where(m => m.UserId != except);

        var members = await query
            .OrderBy(m => m.JoinedAt)
            .Select(m => m.UserId)
            .Take(MaxRecipients)
            .ToListAsync();

        if (members.Count == 0) return [];

        return await permissions.FilterUsersWithChannelPermissionAsync(
            channelId, members, Permissions.ViewChannel);
    }

    private async Task SafelyAsync(string operation, Func<Task> body) =>
        await SafelyAsync(operation, async () =>
        {
            await body();
            return 0;
        });

    /// <summary>Alerts run after the caller has committed, so an exception here would fail a
    /// request whose work is already done and cannot be undone. Logged and swallowed; the sweep
    /// stamps the row either way, because retrying forever is how a house gets buzzed twice.</summary>
    private async Task<int> SafelyAsync(string operation, Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Maintenance alert {Operation} could not be delivered", operation);
            return 0;
        }
    }
}
