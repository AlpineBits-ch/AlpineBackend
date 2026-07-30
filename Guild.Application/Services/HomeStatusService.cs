using System.Text.Json;
using Guild.Application.Dtos.Response;
using Guild.Domain.Enums;
using StackExchange.Redis;

namespace Guild.Application.Services;

/// <summary>Ambient "who's home", stored in Redis with a TTL.</summary>
public class HomeStatusService(IConnectionMultiplexer redisConnection)
{
    private readonly IDatabase _db = redisConnection.GetDatabase();

    private const int DefaultTtlMinutes = 12 * 60;
    private const int MaxTtlMinutes = 7 * 24 * 60;

    private static string GuildKey(string guildId) => $"guild:homestatus:{guildId}";

    private record StoredStatus(string Kind, string? Note, long ExpiresAtUnix);

    public async Task<HomeStatusDto> SetAsync(
        string guildId, string userId, HomeStatusKind kind, string? note, int? expiresInMinutes)
    {
        var ttl = TimeSpan.FromMinutes(Math.Clamp(expiresInMinutes ?? DefaultTtlMinutes, 1, MaxTtlMinutes));
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);

        var stored = new StoredStatus(kind.ToString(), note, expiresAt.ToUnixTimeSeconds());

        // A hash field per user, since Redis can't expire individual fields: the per-user expiry
        // is carried in the value and enforced on read, and the whole hash gets a TTL as a
        // backstop so an abandoned guild's key doesn't live forever.
        await _db.HashSetAsync(GuildKey(guildId), userId, JsonSerializer.Serialize(stored));
        await _db.KeyExpireAsync(GuildKey(guildId), TimeSpan.FromMinutes(MaxTtlMinutes), ExpireWhen.Always);

        return new HomeStatusDto { UserId = userId, Kind = kind, Note = note, ExpiresAt = expiresAt };
    }

    public async Task ClearAsync(string guildId, string userId) =>
        await _db.HashDeleteAsync(GuildKey(guildId), userId);

    /// <summary>Everyone's current status, with expired entries filtered out and lazily reaped.</summary>
    public async Task<List<HomeStatusDto>> GetAsync(string guildId)
    {
        var entries = await _db.HashGetAllAsync(GuildKey(guildId));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var live = new List<HomeStatusDto>();
        var stale = new List<RedisValue>();

        foreach (var entry in entries)
        {
            StoredStatus? stored;
            try
            {
                // Explicit cast: RedisValue converts implicitly to both string and
                // ReadOnlySpan<byte>, which makes the Deserialize overload ambiguous.
                stored = JsonSerializer.Deserialize<StoredStatus>((string)entry.Value!);
            }
            catch (JsonException)
            {
                // Unreadable value from an older shape - drop it rather than fail the whole board.
                stale.Add(entry.Name);
                continue;
            }

            if (stored is null || stored.ExpiresAtUnix <= now)
            {
                stale.Add(entry.Name);
                continue;
            }

            if (!Enum.TryParse<HomeStatusKind>(stored.Kind, out var kind))
            {
                stale.Add(entry.Name);
                continue;
            }

            live.Add(new HomeStatusDto
            {
                UserId = entry.Name!,
                Kind = kind,
                Note = stored.Note,
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(stored.ExpiresAtUnix),
            });
        }

        if (stale.Count > 0) await _db.HashDeleteAsync(GuildKey(guildId), stale.ToArray());

        return live;
    }
}
