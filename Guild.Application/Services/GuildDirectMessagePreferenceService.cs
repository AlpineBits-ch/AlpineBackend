using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Resolves the per-guild DM toggle (privacy spec T2-14) - the stored override where one exists,
/// and the value derived from the user's global <c>DirectMessagePolicy</c> where one does not.
/// </summary>
public class GuildDirectMessagePreferenceService(
    MicroserviceContext ctx,
    PrivacySettingsCache privacySettings)
{
    /// <summary>What a guild with no stored row resolves to.</summary>
    public static bool DefaultFor(DirectMessagePolicy policy) => policy != DirectMessagePolicy.Nobody;

    /// <summary>Every override the user has stored, keyed by guild.</summary>
    public async Task<List<GuildDirectMessagePreference>> GetOverridesAsync(
        string userId, CancellationToken ct = default) =>
        await BuildOverridesQuery(ctx, userId).ToListAsync(ct);

    /// <summary>
    /// The queries are built rather than inlined so they can be checked against the real Npgsql
    /// provider without a database - the EF InMemory provider cannot fail on an untranslatable
    /// query, so behavioural tests alone would not catch one.
    /// </summary>
    public static IQueryable<GuildDirectMessagePreference> BuildOverridesQuery(
        MicroserviceContext ctx, string userId) =>
        ctx.GuildDirectMessagePreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.GuildId);

    /// <summary>The guilds the user is in, narrowed to <paramref name="guildIds"/> when that is
    /// non-empty.</summary>
    public static IQueryable<string> BuildMembershipQuery(
        MicroserviceContext ctx, string userId, IReadOnlyCollection<string> guildIds)
    {
        var query = ctx.GuildMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId);

        if (guildIds.Count > 0)
        {
            var wanted = guildIds.ToList();
            query = query.Where(m => wanted.Contains(m.GuildId));
        }

        return query.Select(m => m.GuildId).Distinct();
    }

    /// <summary>
    /// The effective answer for each of <paramref name="guildIds"/>: the override when one exists,
    /// otherwise <see cref="DefaultFor"/> applied to the user's global policy.
    /// </summary>
    /// <param name="guildIds">Empty means "every guild this user is in".</param>
    public async Task<Dictionary<string, bool>> ResolveAsync(
        string userId, IReadOnlyCollection<string> guildIds, CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(userId)) return resolved;

        var narrowed = guildIds.Count > 0;
        List<string> wanted = narrowed
            ? guildIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList()
            : [];

        if (narrowed && wanted.Count == 0) return resolved;

        var memberGuildIds = await BuildMembershipQuery(ctx, userId, wanted).ToListAsync(ct);

        if (memberGuildIds.Count == 0) return resolved;

        var overrides = await ctx.GuildDirectMessagePreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId && memberGuildIds.Contains(p.GuildId))
            .ToDictionaryAsync(p => p.GuildId, p => p.AllowDirectMessages, StringComparer.Ordinal, ct);

        // Only asked when at least one guild has no override - a user who has explicitly answered
        // for every guild in the batch needs nothing from Identity.
        bool? inherited = null;

        foreach (var guildId in memberGuildIds)
        {
            if (overrides.TryGetValue(guildId, out var stored))
            {
                resolved[guildId] = stored;
                continue;
            }

            inherited ??= DefaultFor((await privacySettings.GetAsync(userId, ct)).DirectMessagePolicy);
            resolved[guildId] = inherited.Value;
        }

        return resolved;
    }

    /// <summary>Upserts the caller's override for one guild.</summary>
    public async Task<GuildDirectMessagePreference?> SetAsync(
        string userId, string guildId, bool allowDirectMessages, CancellationToken ct = default)
    {
        var isMember = await ctx.GuildMembers
            .AsNoTracking()
            .AnyAsync(m => m.UserId == userId && m.GuildId == guildId, ct);

        if (!isMember) return null;

        var existing = await ctx.GuildDirectMessagePreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.GuildId == guildId, ct);

        if (existing is null)
        {
            existing = GuildDirectMessagePreference.Create(userId, guildId, allowDirectMessages);
            ctx.GuildDirectMessagePreferences.Add(existing);
            return existing;
        }

        existing.AllowDirectMessages = allowDirectMessages;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return existing;
    }
}
