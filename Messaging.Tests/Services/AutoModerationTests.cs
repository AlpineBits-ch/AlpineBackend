using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Services;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Services;

/// <summary>
/// Covers AutoModeration's two pure-ish decision points directly (word matching, rate limiting)
/// without needing to fake the whole IMessageBus surface - CheckAsync/GetConfigAsync (the bus-
/// dependent wrapper) is thin enough that these two are where an actual bug would hide.
/// </summary>
[TestFixture]
public class AutoModerationTests
{
    // ══════════════════════════════════════════════════════════════════════════
    // ContainsBlockedWord — whole-word, case-insensitive match
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void ContainsBlockedWord_ExactWholeWordMatch_ReturnsTrue()
    {
        var result = AutoModeration.ContainsBlockedWord("this message contains spam in it", ["spam"]);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ContainsBlockedWord_CaseInsensitive_StillMatches()
    {
        var result = AutoModeration.ContainsBlockedWord("this message contains SPAM in it", ["spam"]);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ContainsBlockedWord_SubstringWithinAnotherWord_DoesNotMatch()
    {
        // "spam" is a blocked word, but "spammer" is a different word entirely -
        // whole-word matching must not flag it.
        var result = AutoModeration.ContainsBlockedWord("please don't spammer me", ["spam"]);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ContainsBlockedWord_NoBlockedWordsConfigured_ReturnsFalse()
    {
        var result = AutoModeration.ContainsBlockedWord("literally anything", []);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ContainsBlockedWord_EmptyContent_ReturnsFalse()
    {
        var result = AutoModeration.ContainsBlockedWord("", ["spam"]);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ContainsBlockedWord_MultipleWords_MatchesAnyOne()
    {
        var result = AutoModeration.ContainsBlockedWord("this has one bad word in it", ["nope", "bad", "also-nope"]);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ContainsBlockedWord_BlankEntryInList_IsSkippedWithoutMatchingEverything()
    {
        // A blank/whitespace-only entry must not degenerate into matching all content
        // (an empty pattern would otherwise match anywhere).
        var result = AutoModeration.ContainsBlockedWord("perfectly fine message", ["", "   ", "actualword"]);
        Assert.That(result, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // IsRateLimitedAsync — fixed window per channel+user
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task IsRateLimited_FirstMessage_NotLimited()
    {
        var cache = new FakeDistributedCache();
        var limited = await AutoModeration.IsRateLimitedAsync("chan-1", "user-1", maxMessages: 3, intervalSeconds: 10, cache);
        Assert.That(limited, Is.False);
    }

    [Test]
    public async Task IsRateLimited_UnderTheLimit_NotLimited()
    {
        var cache = new FakeDistributedCache();
        for (var i = 0; i < 3; i++)
        {
            var limited = await AutoModeration.IsRateLimitedAsync("chan-1", "user-1", maxMessages: 3, intervalSeconds: 10, cache);
            Assert.That(limited, Is.False, $"message #{i + 1} of 3 should not be limited yet");
        }
    }

    [Test]
    public async Task IsRateLimited_ExceedsLimit_ReturnsTrue()
    {
        var cache = new FakeDistributedCache();
        for (var i = 0; i < 3; i++)
            await AutoModeration.IsRateLimitedAsync("chan-1", "user-1", maxMessages: 3, intervalSeconds: 10, cache);

        // 4th message in the same window exceeds maxMessages: 3
        var limited = await AutoModeration.IsRateLimitedAsync("chan-1", "user-1", maxMessages: 3, intervalSeconds: 10, cache);
        Assert.That(limited, Is.True);
    }

    [Test]
    public async Task IsRateLimited_DifferentUsersInSameChannel_TrackedIndependently()
    {
        var cache = new FakeDistributedCache();
        for (var i = 0; i < 3; i++)
            await AutoModeration.IsRateLimitedAsync("chan-1", "user-1", maxMessages: 3, intervalSeconds: 10, cache);

        // user-2 has sent nothing yet in this channel - must not inherit user-1's count.
        var limited = await AutoModeration.IsRateLimitedAsync("chan-1", "user-2", maxMessages: 3, intervalSeconds: 10, cache);
        Assert.That(limited, Is.False);
    }

    [Test]
    public async Task IsRateLimited_SameUserDifferentChannels_TrackedIndependently()
    {
        var cache = new FakeDistributedCache();
        for (var i = 0; i < 3; i++)
            await AutoModeration.IsRateLimitedAsync("chan-1", "user-1", maxMessages: 3, intervalSeconds: 10, cache);

        var limited = await AutoModeration.IsRateLimitedAsync("chan-2", "user-1", maxMessages: 3, intervalSeconds: 10, cache);
        Assert.That(limited, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CheckAsync — the bus/cache-dependent wrapper (config cache hit vs. miss)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CheckAsync_ConfigDisabled_ReturnsNull_RegardlessOfContent()
    {
        var cache = new FakeDistributedCache();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false, BlockedWords = ["spam"] },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await AutoModeration.CheckAsync("chan-1", "user-1", "this is spam", cache, bus);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task CheckAsync_ConfigCacheHit_SkipsBus_UsesCachedConfig()
    {
        var cache = new FakeDistributedCache();
        cache.SetEntry("automod:config:chan-1", "{\"Enabled\":true,\"BlockedWords\":[\"spam\"]}");
        var bus = new FakeMessageBus(); // no responder configured - a bus call would throw

        var result = await AutoModeration.CheckAsync("chan-1", "user-1", "this is spam", cache, bus);

        Assert.That(result, Is.EqualTo("blocked_word"));
    }

    [Test]
    public async Task CheckAsync_ConfigCacheMiss_FetchesFromBus_AndPopulatesCache()
    {
        var cache = new FakeDistributedCache();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = true, BlockedWords = ["spam"] },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await AutoModeration.CheckAsync("chan-1", "user-1", "this is spam", cache, bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo("blocked_word"));
            Assert.That(cache.HasEntry("automod:config:chan-1"), Is.True,
                "The fetched config must be cached so subsequent messages in the channel skip the bus round trip");
        });
    }

    [Test]
    public async Task CheckAsync_EnabledButCleanContent_UnderRateLimit_ReturnsNull()
    {
        var cache = new FakeDistributedCache();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = true, BlockedWords = ["spam"], MaxMessagesPerInterval = 5, IntervalSeconds = 60 },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await AutoModeration.CheckAsync("chan-1", "user-1", "perfectly fine message", cache, bus);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task CheckAsync_EnabledCleanContent_ExceedsRateLimit_ReturnsRateLimited()
    {
        var cache = new FakeDistributedCache();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = true, BlockedWords = [], MaxMessagesPerInterval = 1, IntervalSeconds = 60 },
            _ => throw new InvalidOperationException("unexpected"),
        });

        await AutoModeration.CheckAsync("chan-1", "user-1", "message one", cache, bus);
        var result = await AutoModeration.CheckAsync("chan-1", "user-1", "message two", cache, bus);

        Assert.That(result, Is.EqualTo("rate_limited"));
    }
}
