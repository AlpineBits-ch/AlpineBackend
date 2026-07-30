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
    bool Acknowledged,
    /// <summary>The message carrying the component that was used - set only for a
    /// MESSAGE_COMPONENT interaction, and what UPDATE_MESSAGE (callback type 7) edits in place.
    /// Null for slash commands, which have no originating message.</summary>
    string? MessageId = null,
    /// <summary>Which InteractionType this is, so the callback endpoint can reject a response
    /// shape that makes no sense for it (an UPDATE_MESSAGE against a slash command has nothing
    /// to update).</summary>
    int InteractionType = Bots.Contracts.Gateway.Payloads.InteractionType.ApplicationCommand);

/// <summary>
/// An ephemeral response's components, kept in Redis instead of on a message row - the message
/// itself is never persisted (see DiscordInteractionEndpoint's ephemeral branch), so there is
/// nothing to validate a later button click against.
/// </summary>
public record EphemeralMessageRecord(
    string EphemeralId,
    string BotUserId,
    string? GuildId,
    string ChannelId,
    string InvokingUserId,
    List<string> CustomIds);

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

    private static string EphemeralKey(string ephemeralId) => $"ephemeral:{ephemeralId}";

    public Task SaveEphemeralAsync(EphemeralMessageRecord record) =>
        cache.SetStringAsync(EphemeralKey(record.EphemeralId), JsonSerializer.Serialize(record),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl });

    public async Task<EphemeralMessageRecord?> GetEphemeralAsync(string ephemeralId)
    {
        var json = await cache.GetStringAsync(EphemeralKey(ephemeralId));
        return json is null ? null : JsonSerializer.Deserialize<EphemeralMessageRecord>(json);
    }

    // ── Autocomplete ───────────────────────────────────────────────────────── Autocomplete is the
    // one interaction that needs a synchronous-shaped answer: the client is waiting to render a
    // dropdown.

    private static string AutocompleteKey(string interactionId) => $"autocomplete:{interactionId}";

    private static readonly TimeSpan AutocompleteTtl = TimeSpan.FromSeconds(30);

    public Task SaveAutocompleteResultAsync(string interactionId, string choicesJson) =>
        cache.SetStringAsync(AutocompleteKey(interactionId), choicesJson,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = AutocompleteTtl });

    public Task<string?> GetAutocompleteResultAsync(string interactionId) =>
        cache.GetStringAsync(AutocompleteKey(interactionId));
}
