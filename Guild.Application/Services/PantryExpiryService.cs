using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Tells a pantry what is about to go off, before it does.</summary>
public class PantryExpiryService(
    MicroserviceContext ctx,
    HouseholdAlertService alerts,
    GuildPermissionService permissions,
    ILogger<PantryExpiryService> logger)
{
    /// <summary>Matches PantryConfig's own default, so an unconfigured pantry behaves the same as
    /// one explicitly left at the default.</summary>
    private const int DefaultExpiryWarningDays = 3;

    /// <summary>The ceiling PantryEndpoint enforces on ExpiryWarningDays, so no configured horizon
    /// can reach past what the candidate query looks at.</summary>
    private const int MaxExpiryWarningDays = 90;

    /// <summary>How far past its date an item is still worth mentioning.</summary>
    private static readonly TimeSpan MaxLateness = TimeSpan.FromDays(7);

    private const int BatchSize = 200;

    /// <summary>Warns every pantry with something on the turn.</summary>
    public async Task<int> SendDueWarningsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Nearest date first: an item that is not yet inside its channel's horizon stays a
        // candidate and is simply re-examined next pass, so ordering ascending is what stops the
        // batch being spent on things that are months away.
        var candidates = await ctx.PantryItems
            .Where(i => i.ExpiryNotifiedAt == null
                        && i.ExpiresAt != null
                        && i.ExpiresAt <= now.AddDays(MaxExpiryWarningDays))
            .OrderBy(i => i.ExpiresAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var channelIds = candidates.Select(i => i.ChannelId).Distinct().ToList();

        var horizons = await ctx.PantryConfigs.AsNoTracking()
            .Where(c => channelIds.Contains(c.ChannelId))
            .ToDictionaryAsync(c => c.ChannelId, c => c.ExpiryWarningDays, ct);

        var stale = new List<PantryItem>();
        var due = new List<PantryItem>();

        foreach (var item in candidates)
        {
            var expiresAt = item.ExpiresAt!.Value;

            if (now - expiresAt > MaxLateness)
            {
                stale.Add(item);
                continue;
            }

            var horizon = horizons.GetValueOrDefault(item.ChannelId, DefaultExpiryWarningDays);
            if (expiresAt > now.AddDays(horizon)) continue;

            due.Add(item);
        }

        foreach (var item in stale) item.ExpiryNotifiedAt = now;

        var warned = 0;

        foreach (var group in due.GroupBy(i => i.ChannelId, StringComparer.Ordinal))
        {
            var items = group.OrderBy(i => i.ExpiresAt).ToList();
            var guildId = items[0].GuildId;

            // The module can be switched off between an item being added and it going off, and a
            // guild with the pantry disabled must not be told about its contents.
            if (!await permissions.IsFeatureEnabledAsync(guildId, GuildFeatures.Pantry)) continue;

            await alerts.PantryExpiringAsync(guildId, group.Key, items);

            // Stamped whether or not anyone was eligible to receive it.
            foreach (var item in items) item.ExpiryNotifiedAt = now;
            warned += items.Count;
        }

        await ctx.SaveChangesAsync(ct);

        if (warned > 0 || stale.Count > 0)
            logger.LogDebug("Pantry expiry sweep warned {Warned} items and retired {Stale}", warned, stale.Count);

        return warned + stale.Count;
    }
}
