using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Bots.Application.Gateway;

/// <summary>Correlates an invoked interaction (identified by its unguessable token - the same
/// thing that authenticates the callback/followup calls, matching real Discord) with the guild/
/// channel context needed to actually post the bot's response as a real message. Redis-backed,
/// not a DB entity - this is inherently short-lived (Discord's own followup token lifetime is
/// 15 minutes), same reasoning as GatewaySession not being persisted.</summary>
public record PendingInteraction(
    string InteractionId,
    string BotUserId,
    string? GuildId,
    string ChannelId,
    string InvokingUserId,
    string CommandName,
    bool Acknowledged);

public class PendingInteractionStore(IDistributedCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private static string CacheKey(string token) => $"interaction:{token}";

    public Task SaveAsync(string token, PendingInteraction interaction) =>
        cache.SetStringAsync(CacheKey(token), JsonSerializer.Serialize(interaction),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl });

    public async Task<PendingInteraction?> GetAsync(string token)
    {
        var json = await cache.GetStringAsync(CacheKey(token));
        return json is null ? null : JsonSerializer.Deserialize<PendingInteraction>(json);
    }

    public Task MarkAcknowledgedAsync(string token, PendingInteraction current) =>
        SaveAsync(token, current with { Acknowledged = true });
}
