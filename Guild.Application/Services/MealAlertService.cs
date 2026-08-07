using Guild.Contracts;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Tells whoever is down to cook that it is today, once, in the morning.</summary>
public class MealAlertService(
    MicroserviceContext ctx,
    HouseholdNotifier notifier,
    GuildPermissionService permissions,
    ILogger<MealAlertService> logger)
{
    /// <summary>The cook has something on today.</summary>
    public const string KindCookingToday = "meals.cooking_today";

    /// <summary>Local time of day the reminder aims for.</summary>
    private const int MorningMinuteLocal = 8 * 60;

    /// <summary>How far past the intended instant a reminder is still worth sending.</summary>
    private static readonly TimeSpan MaxLateness = TimeSpan.FromHours(12);

    private const int BatchSize = 200;

    /// <summary>
    /// Whether an entry is a candidate for the cooking-today alert at all, given the calendar date
    /// the sweep is running on.
    /// </summary>
    public static bool ShouldNotify(MealPlanEntry entry, DateOnly today) =>
        entry.CookUserId is not null && entry.NotifiedAt is null && entry.Date == today;

    /// <summary>Sends the day's cooking reminders.</summary>
    public async Task<int> SendCookingTodayAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var candidates = await ctx.Set<MealPlanEntry>()
            .Where(e => e.NotifiedAt == null && e.CookUserId != null && e.Date <= today)
            // Oldest first, so a backlog of abandoned plans is stamped out of the filtered index in
            // batches rather than being re-read every pass forever.
            .OrderBy(e => e.Date)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var guildIds = candidates.Select(e => e.GuildId).Distinct().ToList();

        var quietHours = await ctx.GuildQuietHoursConfigs.AsNoTracking()
            .Where(c => guildIds.Contains(c.GuildId))
            .ToDictionaryAsync(c => c.GuildId, ct);

        var recipeIds = candidates
            .Where(e => e.RecipeId != null)
            .Select(e => e.RecipeId!)
            .Distinct()
            .ToList();

        var recipeTitles = new Dictionary<string, string>();

        if (recipeIds.Count > 0)
        {
            recipeTitles = await ctx.Set<Recipe>().AsNoTracking()
                .Where(r => recipeIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Title })
                .ToDictionaryAsync(r => r.Id, r => r.Title, ct);
        }

        var handled = 0;

        foreach (var entry in candidates)
        {
            if (!ShouldNotify(entry, today))
            {
                // Yesterday's dinner, or earlier.
                entry.NotifiedAt = now;
                handled++;
                continue;
            }

            quietHours.TryGetValue(entry.GuildId, out var config);

            var fireAt = MorningInstant(entry.Date, config);
            if (config is not null) fireAt = config.DeferPast(fireAt);

            // Still too early in this house's day.
            if (fireAt > now) continue;

            if (now - fireAt > MaxLateness)
            {
                entry.NotifiedAt = now;
                handled++;
                continue;
            }

            if (await SendAsync(entry, recipeTitles))
            {
                entry.NotifiedAt = now;
                handled++;
            }
        }

        await ctx.SaveChangesAsync(ct);

        if (handled > 0) logger.LogDebug("Handled {Count} cooking-today reminders", handled);

        return handled;
    }

    /// <summary>Delivers one reminder.</summary>
    private async Task<bool> SendAsync(MealPlanEntry entry, Dictionary<string, string> recipeTitles)
    {
        try
        {
            // The cook still has to be able to see the channel the plan is on.
            var recipients = await permissions.FilterUsersWithChannelPermissionAsync(
                entry.ChannelId, [entry.CookUserId!], Permissions.ViewChannel);

            if (recipients.Count == 0) return true;

            var recipeTitle = entry.RecipeId is not null
                ? recipeTitles.GetValueOrDefault(entry.RecipeId)
                : null;

            var subject = recipeTitle ?? entry.FreeText;

            await notifier.AlertAsync(
                entry.GuildId, entry.ChannelId, recipients,
                KindCookingToday,
                // The dish itself when there is one, because "Thai curry" is the whole reminder and
                // a generic title makes the recipient open the app to learn what they agreed to.
                subject is null
                    ? AlertText.Loc(HouseholdLocKeys.MealCookingTodayTitle, "You're down to cook")
                    : AlertText.Raw(subject),
                AlertText.Loc(HouseholdLocKeys.MealCookingTodayBody, "You're down to cook this today."),
                entry.Id,
                new { entry.Date, entry.Slot, entry.RecipeId, entry.Servings });

            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Cooking-today reminder for {EntryId} could not be delivered", entry.Id);
            return false;
        }
    }

    /// <summary>
    /// The instant <see cref="MorningMinuteLocal"/> falls on, in the house's own time zone.
    /// </summary>
    private static DateTimeOffset MorningInstant(DateOnly date, GuildQuietHoursConfig? config)
    {
        var local = date.ToDateTime(TimeOnly.MinValue).AddMinutes(MorningMinuteLocal);

        if (config is null) return new DateTimeOffset(local, TimeSpan.Zero);

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A bad tz id must not stop a reminder from ever firing - degrade to UTC rather than
            // computing an instant nobody's morning corresponds to.
            return new DateTimeOffset(local, TimeSpan.Zero);
        }
    }
}
