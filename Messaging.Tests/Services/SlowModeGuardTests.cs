using Messaging.Application.Services;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.Caching.Distributed;

namespace Messaging.Tests.Services;

/// <summary>
/// Covers SlowModeGuard directly - it needs only a fakeable IDistributedCache, so the whole
/// decision (window off, first send, second send inside the window, second send after it) is
/// reachable without the bus or the endpoint around it.
/// </summary>
[TestFixture]
public class SlowModeGuardTests
{
    private FakeDistributedCache _cache = null!;

    private const string ChannelId = "chan-1";
    private const string UserId = "user-1";

    [SetUp]
    public void SetUp() => _cache = new FakeDistributedCache();

    [Test]
    public async Task CheckAsync_SlowModeDisabled_AlwaysAllows_AndRecordsNothing()
    {
        var first = await SlowModeGuard.CheckAsync(ChannelId, UserId, 0, _cache);
        var second = await SlowModeGuard.CheckAsync(ChannelId, UserId, 0, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Null);
            Assert.That(second, Is.Null, "a disabled window must not start tracking sends");
        });
    }

    [Test]
    public async Task CheckAsync_NegativeSlowMode_TreatedAsDisabled()
    {
        var result = await SlowModeGuard.CheckAsync(ChannelId, UserId, -5, _cache);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task CheckAsync_FirstSend_Allows()
    {
        var result = await SlowModeGuard.CheckAsync(ChannelId, UserId, 30, _cache);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task CheckAsync_SecondSendInsideWindow_ReturnsRemainingSeconds()
    {
        await SlowModeGuard.CheckAsync(ChannelId, UserId, 30, _cache);

        var result = await SlowModeGuard.CheckAsync(ChannelId, UserId, 30, _cache);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.GreaterThan(0).And.LessThanOrEqualTo(30));
    }

    [Test]
    public async Task CheckAsync_SecondSendAfterWindowElapsed_Allows()
    {
        // Backdate the recorded send past the window instead of sleeping - the guard reads the
        // stored timestamp, so time can be simulated by writing an older one.
        await SlowModeGuard.MarkSentAsync(ChannelId, UserId, 30, _cache);
        await _cache.SetStringAsync(
            $"slowmode:{ChannelId}:{UserId}",
            DateTimeOffset.UtcNow.AddSeconds(-31).UtcTicks.ToString());

        var result = await SlowModeGuard.CheckAsync(ChannelId, UserId, 30, _cache);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task CheckAsync_IsPerUser_OneUsersSendDoesNotBlockAnother()
    {
        await SlowModeGuard.CheckAsync(ChannelId, UserId, 30, _cache);

        var other = await SlowModeGuard.CheckAsync(ChannelId, "user-2", 30, _cache);

        Assert.That(other, Is.Null);
    }

    [Test]
    public async Task CheckAsync_IsPerChannel_OneChannelsSendDoesNotBlockAnother()
    {
        await SlowModeGuard.CheckAsync(ChannelId, UserId, 30, _cache);

        var other = await SlowModeGuard.CheckAsync("chan-2", UserId, 30, _cache);

        Assert.That(other, Is.Null);
    }

    [Test]
    public async Task CheckAsync_CorruptStoredTimestamp_FailsOpenAndReseeds()
    {
        await _cache.SetStringAsync($"slowmode:{ChannelId}:{UserId}", "not-a-number");

        var result = await SlowModeGuard.CheckAsync(ChannelId, UserId, 30, _cache);

        Assert.That(result, Is.Null, "an unparseable value must not lock the author out permanently");

        var next = await SlowModeGuard.CheckAsync(ChannelId, UserId, 30, _cache);
        Assert.That(next, Is.Not.Null, "and the reseeded timestamp must then be enforced");
    }
}
