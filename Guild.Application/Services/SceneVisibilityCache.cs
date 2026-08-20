using System.Text.Json;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Services;

/// <summary>
/// Which of a guild's scene channels only their cast may see, and whether one caller is in it.
/// <see cref="GuildPermissionService.ComputePermissionsForUserAsync"/> resolves thread-shaped
/// channels by copying their parent's mask and skipping channel overwrites, so a ChannelPermission
/// row on a scene is ignored; visibility is checked beside the permission answer instead of being
/// expressed as one.
/// </summary>
public class SceneVisibilityCache(
    IDistributedCache cache, MicroserviceContext ctx, PersonaService personas)
{
    private const string Version = "v1";

    /// <summary>Per-scope memo, so a fan-out loop over hundreds of recipients reads Redis once.</summary>
    private readonly Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> _memo =
        new(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> None =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private static string Encode(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>Keys the guild's map behind one token, so a single write drops it - the pattern
    /// <see cref="PersonaService"/> uses for its per-user sets.</summary>
    private static string EpochKey(string guildId) => $"guild:{Version}:{Encode(guildId)}:sceneaccessepoch";

    private static string MapKey(string guildId, string epoch) =>
        $"guild:{Version}:{Encode(guildId)}:restrictedscenes:{epoch}";

    /// <summary>
    /// Drops the guild's map. Called from every write that can change who may see a scene: its
    /// visibility, the cast of a restricted one, and its removal.
    /// </summary>
    /// <param name="guildId">The guild whose map is stale.</param>
    public async Task InvalidateGuildAsync(string guildId)
    {
        _memo.Remove(guildId);

        await cache.SetStringAsync(EpochKey(guildId), Guid.NewGuid().ToString("N"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) });
    }

    /// <summary>
    /// The guild's cast-only scenes, each mapped to its cast, with the out-of-character companion
    /// thread carrying the same entry. Empty in a guild with no private scenes, which is the common
    /// case and the whole reason this is one dictionary lookup on the permission path.
    /// </summary>
    /// <param name="guildId">The guild being read.</param>
    /// <returns>Cast persona ids keyed by channel.</returns>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RestrictedAsync(string guildId)
    {
        if (_memo.TryGetValue(guildId, out var memoized)) return memoized;

        var epoch = await cache.GetStringAsync(EpochKey(guildId)) ?? "0";
        var key = MapKey(guildId, epoch);

        var cached = await cache.GetStringAsync(key);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(cached);
            return _memo[guildId] = Project(deserialized);
        }

        var rows = await ctx.Set<SceneState>()
            .AsNoTracking()
            .Where(s => s.GuildId == guildId && s.Visibility == SceneVisibility.Cast)
            .Select(s => new { s.ChannelId, s.OocThreadId, s.ParticipantPersonaIds })
            .ToListAsync();

        var computed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            computed[row.ChannelId] = row.ParticipantPersonaIds;

            // The companion thread follows the scene's visibility, never its own: it is where the
            // cast talks about the scene.
            if (!string.IsNullOrWhiteSpace(row.OocThreadId))
                computed[row.OocThreadId] = row.ParticipantPersonaIds;
        }

        // A minute rather than the fifteen the permission caches use. The epoch is moved before the
        // write it describes commits, so a read landing in that window can cache a map without the
        // scene in it, and the direction that fails open is a private scene everybody can see.
        await cache.SetStringAsync(key, JsonSerializer.Serialize(computed), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1),
        });

        return _memo[guildId] = Project(computed);
    }

    /// <summary>
    /// Whether one caller may see one channel. Everything outside the map is somebody else's
    /// problem and answers true.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="guildId">The guild the channel is in.</param>
    /// <param name="channelId">The channel being read.</param>
    /// <param name="isGameMaster">Whether the caller holds ManageScenes here.</param>
    /// <returns>True unless the channel is a cast-only scene the caller has nobody in.</returns>
    public async Task<bool> CanSeeAsync(
        string userId, string guildId, string channelId, bool isGameMaster)
    {
        var restricted = await RestrictedAsync(guildId);
        if (!restricted.TryGetValue(channelId, out var cast)) return true;

        if (isGameMaster) return true;
        if (cast.Count == 0) return false;

        var speakable = await SpeakableIdsAsync(userId, guildId);
        return cast.Any(speakable.Contains);
    }

    /// <summary>
    /// The characters the caller answers for here. Retired ones count: a character that has been
    /// put away was still in the scene, and dropping it would take the log away from whoever played
    /// it.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="guildId">The guild.</param>
    /// <returns>The persona ids, as a set the cast is intersected against.</returns>
    public async Task<HashSet<string>> SpeakableIdsAsync(string userId, string guildId) =>
        (await personas.GetUsablePersonasAsync(userId, guildId))
        .Select(p => p.PersonaId)
        .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Project(
        Dictionary<string, List<string>>? source)
    {
        if (source is null || source.Count == 0) return None;

        return source.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.Ordinal);
    }
}
