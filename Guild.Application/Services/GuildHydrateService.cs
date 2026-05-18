using System.Text.Json;
using Guild.Application.Dtos.Response;
using Guild.Persistence.Persistence;
using StackExchange.Redis;

namespace Guild.Application.Services;

public record MemberPresenceState
{
    public string MemberId { get; init; }
    public string UserId { get; init; }
    public string Status { get; init; }
    public string? Activity { get; init; }
    public Dictionary<string, string>? ClientStatus { get; init; }
    public long HeartbeatTimestamp { get; init; }
}

public class GuildHydrateService(
    IConnectionMultiplexer redisConnection,
    ILogger<GuildHydrateService> logger)
{
    private readonly IDatabase _db = redisConnection.GetDatabase();

    private const string AddPresenceLuaScript = @"
        -- KEYS[1]: User presence hash key (e.g., presence:user:123)
        -- KEYS[2]: Guild ZSET key (e.g., guild:presence:100)
        -- ARGV[1]: User ID
        -- ARGV[2]: Serialized UserPresenceState
        -- ARGV[3]: Expiry Timestamp (Unix seconds)

        redis.call('HSET', KEYS[1], 'state', ARGV[2])
        redis.call('EXPIRE', KEYS[1], 300) -- Fallback key expiration

        redis.call('ZADD', KEYS[2], ARGV[3], ARGV[1])
        
        return 1
    ";

    public async Task<GuildDto> HydrateGuildForUserIdAsync(string guildId, string userId)
    {
        return new GuildDto();
    }

    public async Task<GuildDto> GetHydratedGuildAsync(string guildId, string userId)
    {
        return new GuildDto();
    }
    
    public async Task<IReadOnlyDictionary<string, MemberPresenceState>> GetPresenceByMemberIdsAsync(
        string guildId, 
        IEnumerable<string> memberIds)
    {
        await PruneExpiredMembersAsync(guildId);
        string guildZsetKey = $"guild:presence:{guildId}";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        IBatch batch = _db.CreateBatch();

        
        Dictionary<string, Task<HashEntry[]>> fetchTasks = memberIds.ToDictionary(
            memberId => memberId,
            memberId => batch.HashGetAllAsync($"presence:user:{memberId}")
        );

        batch.Execute();

        await Task.WhenAll(fetchTasks.Values);

        Dictionary<string, MemberPresenceState> results = [];

        foreach ((string memberId, Task<HashEntry[]> task) in fetchTasks)
        {
            HashEntry[] entries = await task;
            RedisValue stateJson = entries.FirstOrDefault(e => e.Name == "state").Value;

            if (stateJson.IsNullOrEmpty) continue;

            MemberPresenceState? state = JsonSerializer.Deserialize<MemberPresenceState>((string)stateJson!);

            if (state is not null)
                results[memberId] = state;
        }

        return results;
    }
    public async Task<IReadOnlyList<MemberPresenceState>> GetGuildPresenceAsync(string guildId)
    {
        string guildZsetKey = $"guild:presence:{guildId}";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        RedisValue[] activeUserIds = await _db.SortedSetRangeByScoreAsync(
            guildZsetKey,
            start: now,
            stop: double.PositiveInfinity
        );

        if (activeUserIds.Length == 0)
            return [];

        IBatch batch = _db.CreateBatch();

        List<Task<HashEntry[]>> fetchTasks = activeUserIds
            .Select(userId => batch.HashGetAllAsync($"presence:user:{userId}"))
            .ToList();

        batch.Execute();

        await Task.WhenAll(fetchTasks);

        List<MemberPresenceState> results = [];

        foreach (Task<HashEntry[]> task in fetchTasks)
        {
            HashEntry[] entries = await task;
            RedisValue stateJson = entries.FirstOrDefault(e => e.Name == "state").Value;

            if (stateJson.IsNullOrEmpty)
                continue;

            MemberPresenceState? state = JsonSerializer.Deserialize<MemberPresenceState>((string)stateJson!);

            if (state is not null)
                results.Add(state);
        }

        return results;
    }

    public async Task AddPresenceStateAsync(string guildId, MemberPresenceState memberPresence)
    {
        string presenceKey = $"presence:user:{memberPresence.MemberId}";
        string guildZsetKey = $"guild:presence:{guildId}";

        long expiryTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;
        string serializedState = JsonSerializer.Serialize(memberPresence);

        await _db.ScriptEvaluateAsync(
            AddPresenceLuaScript,
            [new RedisKey(presenceKey), new RedisKey(guildZsetKey)],
            [memberPresence.MemberId, serializedState, expiryTimestamp]
        );
    }

    public async Task PruneExpiredMembersAsync(string guildId)
    {
        string guildZsetKey = $"guild:presence:{guildId}";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await _db.SortedSetRemoveRangeByScoreAsync(guildZsetKey, 0, now);
    }
}