using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Echo.E2E.Tests.Fixtures;
using StackExchange.Redis;

namespace Echo.E2E.Tests.Support;

/// <summary>
/// Watches household alerts actually leave the Guild process, by listening to the SignalR Redis
/// backplane the spawned services share.
/// </summary>
internal sealed class HouseholdAlertSpy : IAsyncDisposable
{
    /// <summary>The realtime event name every household alert arrives on - one event for every
    /// kind, which is also what makes a single subscription enough here.</summary>
    private const string AlertEventName = "guild.HouseholdAlert";

    /// <summary>A bound, so a runaway publisher cannot turn this into a memory leak.</summary>
    private const int MaxCaptured = 5000;

    private static readonly Regex KindPattern =
        new("\"kind\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TargetPattern =
        new("\"targetId\"\\s*:\\s*(?:\"([^\"]*)\"|null)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GuildPattern =
        new("\"guildId\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The user id SignalR addressed this frame to, taken off the channel name.</summary>
    private static readonly Regex UserChannelPattern =
        new(":user:(.+)$", RegexOptions.Compiled);

    private readonly ConnectionMultiplexer _redis;
    private readonly ConcurrentQueue<Alert> _captured = new();

    /// <summary>One observed household alert: who it was addressed to, what it was about.</summary>
    public sealed record Alert(string RecipientUserId, string GuildId, string Kind, string? TargetId, string Channel);

    private HouseholdAlertSpy(ConnectionMultiplexer redis) => _redis = redis;

    public static async Task<HouseholdAlertSpy> StartAsync()
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(
            $"{EchoInfraFixture.Default.RedisHost}:{EchoInfraFixture.Default.RedisPort}," +
            $"password={EchoInfraSet.RedisPassword}");

        var spy = new HouseholdAlertSpy(redis);

        // Every channel, rather than a guessed prefix.
        await redis.GetSubscriber().SubscribeAsync(RedisChannel.Pattern("*"), spy.OnMessage);

        return spy;
    }

    private void OnMessage(RedisChannel channel, RedisValue message)
    {
        if (_captured.Count >= MaxCaptured) return;

        var text = Decode(message);
        if (text is null || !text.Contains(AlertEventName, StringComparison.Ordinal)) return;

        var user = UserChannelPattern.Match(channel.ToString());
        if (!user.Success) return;

        _captured.Enqueue(new Alert(
            user.Groups[1].Value,
            Capture(GuildPattern, text) ?? "",
            Capture(KindPattern, text) ?? "",
            Capture(TargetPattern, text),
            channel.ToString()));
    }

    /// <summary>Lossy on purpose: the frame is MessagePack, so its framing bytes are not valid
    /// UTF-8 and decode to replacement characters. The JSON inside is contiguous and survives
    /// intact, which is all that is being read.</summary>
    private static string? Decode(RedisValue message)
    {
        var bytes = (byte[]?)message;
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static string? Capture(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        return match.Success && match.Groups[1].Success ? match.Groups[1].Value : null;
    }

    public IReadOnlyList<Alert> Captured => _captured.ToArray();

    /// <summary>Every alert of one kind about one row, whoever it went to.</summary>
    public IReadOnlyList<Alert> For(string kind, string targetId) => _captured
        .Where(a => a.Kind == kind && a.TargetId == targetId)
        .ToList();

    /// <summary>Who was told about one row, deduplicated - the answer to "and nobody else".</summary>
    public IReadOnlyList<string> RecipientsOf(string kind, string targetId) => _captured
        .Where(a => a.Kind == kind && a.TargetId == targetId)
        .Select(a => a.RecipientUserId)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>Waits for an alert to arrive.</summary>
    public async Task<Alert> WaitForAsync(Func<Alert, bool> predicate, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_captured.FirstOrDefault(predicate) is { } found) return found;
            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"No household alert matching '{what}' within {timeout}.\nObserved:\n" +
            (_captured.IsEmpty
                ? "  (nothing at all - is the sweep interval overridden for this stack?)"
                : string.Join("\n", _captured.Select(a => $"  {a.Kind} -> {a.TargetId} for {a.RecipientUserId}"))));
    }

    /// <summary>Waits for something that should never happen, and passes when it does not. Used for
    /// the lateness cutoffs, where the assertion is an absence.</summary>
    public async Task<bool> NoneArrivedAsync(Func<Alert, bool> predicate, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;

        while (DateTime.UtcNow < deadline)
        {
            if (_captured.Any(predicate)) return false;
            await Task.Delay(200);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _redis.GetSubscriber().UnsubscribeAllAsync();
        await _redis.DisposeAsync();
    }
}
