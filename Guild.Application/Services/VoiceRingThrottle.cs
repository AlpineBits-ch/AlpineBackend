using Microsoft.Extensions.Caching.Distributed;

namespace Guild.Application.Services;

/// <summary>Why a ring was refused, as the wire string the client shows a message for.</summary>
public static class VoiceRingRefusal
{
    /// <summary>This inviter has rung too many people too quickly.</summary>
    public const string InviterFlooding = "InviterFlooding";

    /// <summary>This target has been rung by too many people too quickly.</summary>
    public const string TargetSaturated = "TargetSaturated";

    /// <summary>This target already turned this inviter down, recently enough that asking again is
    /// not a second attempt but a second no.</summary>
    public const string RecentlyDeclined = "RecentlyDeclined";
}

/// <summary>The answer, plus how long the caller has to wait for a different one.</summary>
public readonly record struct VoiceRingThrottleVerdict(bool Allowed, string? Reason, TimeSpan RetryAfter)
{
    public static VoiceRingThrottleVerdict Allow() => new(true, null, TimeSpan.Zero);
    public static VoiceRingThrottleVerdict Refuse(string reason, TimeSpan retryAfter) =>
        new(false, reason, retryAfter);
}

/// <summary>What stops the ring endpoint from being a harassment tool.</summary>
public class VoiceRingThrottle(IDistributedCache cache)
{
    /// <summary>How many rings one account may send in <see cref="Window"/>, to anybody.</summary>
    public const int MaxPerInviter = 6;

    /// <summary>How many rings one account may receive in <see cref="Window"/>, from anybody.</summary>
    public const int MaxPerTarget = 4;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a decline shuts the inviter out for, by how many times this pair has been through
    /// it.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> DeclineCooldowns =
    [
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(24),
    ];

    /// <summary>How long the pair's decline count is remembered.</summary>
    public static readonly TimeSpan DeclineMemory = TimeSpan.FromHours(48);

    /// <summary>Swappable so a test can walk a cooldown forward without sleeping through it.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    private static string InviterKey(string inviterId) => $"voice:ring:rl:from:{inviterId}";
    private static string TargetKey(string targetId) => $"voice:ring:rl:to:{targetId}";
    private static string CooldownKey(string targetId, string inviterId) => $"voice:ring:cooldown:{targetId}:{inviterId}";
    private static string DeclineCountKey(string targetId, string inviterId) => $"voice:ring:declines:{targetId}:{inviterId}";

    /// <summary>
    /// Decides whether this ring may go out, and charges it against both budgets if it may.
    /// </summary>
    public async Task<VoiceRingThrottleVerdict> TryAcquireAsync(
        string inviterId, string targetId, CancellationToken ct = default)
    {
        var now = Clock.GetUtcNow();

        var cooldownRaw = await cache.GetStringAsync(CooldownKey(targetId, inviterId), ct);
        if (long.TryParse(cooldownRaw, out var readyAtTicks))
        {
            // Computed from the stored instant rather than from the key merely existing: Redis TTL
            // resolution is too coarse to tell a client how long is left, and "some time under two
            // hours" is not something a UI can count down.
            var readyAt = new DateTimeOffset(readyAtTicks, TimeSpan.Zero);
            if (readyAt > now)
                return VoiceRingThrottleVerdict.Refuse(VoiceRingRefusal.RecentlyDeclined, readyAt - now);
        }

        var fromCount = await ReadCountAsync(InviterKey(inviterId), ct);
        if (fromCount >= MaxPerInviter)
            return VoiceRingThrottleVerdict.Refuse(VoiceRingRefusal.InviterFlooding, Window);

        var toCount = await ReadCountAsync(TargetKey(targetId), ct);
        if (toCount >= MaxPerTarget)
            return VoiceRingThrottleVerdict.Refuse(VoiceRingRefusal.TargetSaturated, Window);

        await WriteCountAsync(InviterKey(inviterId), fromCount + 1, ct);
        await WriteCountAsync(TargetKey(targetId), toCount + 1, ct);

        return VoiceRingThrottleVerdict.Allow();
    }

    /// <summary>
    /// Records that this target turned this inviter down, and shuts the pair for however long that
    /// makes it.
    /// </summary>
    public async Task<TimeSpan> RecordDeclineAsync(string inviterId, string targetId, CancellationToken ct = default)
    {
        var previous = await ReadCountAsync(DeclineCountKey(targetId, inviterId), ct);
        var count = previous + 1;

        var cooldown = DeclineCooldowns[Math.Min(count, DeclineCooldowns.Count) - 1];

        await WriteCountAsync(DeclineCountKey(targetId, inviterId), count, ct, DeclineMemory);
        await cache.SetStringAsync(
            CooldownKey(targetId, inviterId),
            Clock.GetUtcNow().Add(cooldown).UtcTicks.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = cooldown },
            ct);

        return cooldown;
    }

    /// <summary>Hands a ring's budget back, for a ring that was never sent.</summary>
    public async Task RefundAsync(string inviterId, string targetId, CancellationToken ct = default)
    {
        var fromCount = await ReadCountAsync(InviterKey(inviterId), ct);
        if (fromCount > 0) await WriteCountAsync(InviterKey(inviterId), fromCount - 1, ct);

        var toCount = await ReadCountAsync(TargetKey(targetId), ct);
        if (toCount > 0) await WriteCountAsync(TargetKey(targetId), toCount - 1, ct);
    }

    private async Task<int> ReadCountAsync(string key, CancellationToken ct)
    {
        var raw = await cache.GetStringAsync(key, ct);
        return int.TryParse(raw, out var count) ? count : 0;
    }

    private Task WriteCountAsync(string key, int count, CancellationToken ct, TimeSpan? ttl = null) =>
        cache.SetStringAsync(key, count.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl ?? Window }, ct);
}
