using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Progression;

/// <summary>The isle-scoped settings a caller may change.</summary>
public sealed record PlayerPreferencesUpdate(
    bool NotifyServerStatus,
    bool NotifyQuestComplete,
    bool NotifyDinoDeath,
    bool ShowOnLeaderboard,
    bool PublicProfile);

/// <summary>Reads and writes <see cref="PlayerPreferences"/>.</summary>
public sealed class PlayerPreferencesService(MicroserviceContext context)
{
    /// <summary>A player's settings, defaulted when they have none.</summary>
    public async Task<PlayerPreferences> GetAsync(string playerId, CancellationToken ct = default)
    {
        var stored = await context.PlayerPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(preferences => preferences.PlayerId == playerId, ct);

        return stored ?? PlayerPreferences.For(playerId);
    }

    /// <summary>Settings for a batch of players, defaulted for the ones with no row.</summary>
    public async Task<IReadOnlyDictionary<string, PlayerPreferences>> GetAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, PlayerPreferences>(StringComparer.Ordinal);
        if (playerIds.Count == 0) return resolved;

        var stored = await context.PlayerPreferences
            .AsNoTracking()
            .Where(preferences => playerIds.Contains(preferences.PlayerId))
            .ToListAsync(ct);

        foreach (var preferences in stored)
            resolved[preferences.PlayerId] = preferences;

        foreach (var playerId in playerIds)
        {
            if (!resolved.ContainsKey(playerId))
                resolved[playerId] = PlayerPreferences.For(playerId);
        }

        return resolved;
    }

    /// <summary>Applies an update, creating the row on first save. Returns what is now stored.</summary>
    public async Task<PlayerPreferences> SaveAsync(
        string playerId, PlayerPreferencesUpdate update, CancellationToken ct = default)
    {
        var stored = await context.PlayerPreferences
            .FirstOrDefaultAsync(preferences => preferences.PlayerId == playerId, ct);

        if (stored is null)
        {
            stored = PlayerPreferences.For(playerId);
            context.PlayerPreferences.Add(stored);
        }

        stored.NotifyServerStatus = update.NotifyServerStatus;
        stored.NotifyQuestComplete = update.NotifyQuestComplete;
        stored.NotifyDinoDeath = update.NotifyDinoDeath;
        stored.ShowOnLeaderboard = update.ShowOnLeaderboard;
        stored.PublicProfile = update.PublicProfile;

        await context.SaveChangesAsync(ct);
        return stored;
    }
}
