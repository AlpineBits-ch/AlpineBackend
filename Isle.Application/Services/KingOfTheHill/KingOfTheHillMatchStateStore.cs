using System.Text.Json;
using Isle.Domain.Aggregates;
using StackExchange.Redis;

namespace Isle.Api.Services.KingOfTheHill;

/// <summary>Durable snapshot of the currently running match, read back on process start to resume it.</summary>
public sealed record KothMatchState(
    string DefinitionId,
    string InstanceId,
    DateTime StartedAt,
    IReadOnlyList<string> ParticipantIds);

/// <summary>The one durable "a match is live" marker.</summary>
public sealed class KingOfTheHillMatchStateStore(IConnectionMultiplexer redis, ILogger<KingOfTheHillMatchStateStore> logger)
{
    private const string Key = "isle:koth:active";

    private IDatabase Db => redis.GetDatabase();

    public async Task WriteAsync(KothMatchState state)
    {
        try
        {
            await Db.StringSetAsync(Key, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist the KOTH match marker for {InstanceId}", state.InstanceId);
        }
    }

    public async Task<KothMatchState?> ReadAsync()
    {
        try
        {
            var raw = await Db.StringGetAsync(Key);
            return raw.HasValue ? JsonSerializer.Deserialize<KothMatchState>(raw.ToString()) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the KOTH match marker; treating it as no match running");
            return null;
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await Db.KeyDeleteAsync(Key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear the KOTH match marker");
        }
    }
}
