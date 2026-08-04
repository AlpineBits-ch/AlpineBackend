using System.Text.Json;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Isle.Api.Services.Privacy;
using Isle.Tests.Helpers;
using NSubstitute;
using Wolverine;

namespace Isle.Tests.Tests.Services.Privacy;

/// <summary>
/// Covers Isle's copy of the §1.4 privacy cache. The behaviour that matters is the fallback ladder:
/// a fresh cached value is served without asking Identity, a stale one is preferred over a guess
/// when Identity cannot be reached, and with nothing at all the answer denies rather than permits.
/// </summary>
[TestFixture]
public class PrivacySettingsCacheTests
{
    private const string UserId = "user-1";

    // ── normal ────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_CacheMiss_AsksIdentityAndCachesTheAnswer()
    {
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, true)]);

        var settings = await bundle.Settings.GetAsync(UserId);

        Assert.Multiple(() =>
        {
            Assert.That(settings.AllowPositionalVoiceCapture, Is.True);
            Assert.That(bundle.Cache.HasEntry(PrivacySettingsCache.KeyFor(UserId)), Is.True);
        });
    }

    [Test]
    public async Task GetAsync_SecondReadWithinTheRefreshWindow_DoesNotAskIdentityAgain()
    {
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, true)]);

        await bundle.Settings.GetAsync(UserId);
        await bundle.Settings.GetAsync(UserId);

        await bundle.Bus.Received(1).InvokeAsync<GetUserPrivacySettingsResponse>(
            Arg.Any<GetUserPrivacySettingsRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Test]
    public async Task GetAsync_ManyIds_ReturnsOneEntryPerRequestedId()
    {
        var bundle = PrivacyTestFactory.Build([
            PrivacyTestFactory.WithPositionalVoice("a", true),
            PrivacyTestFactory.WithPositionalVoice("b", false),
        ]);

        var result = await bundle.Settings.GetAsync(["a", "b", "c"]);

        Assert.That(result.Keys, Is.EquivalentTo(new[] { "a", "b", "c" }));
        Assert.That(result["b"].AllowPositionalVoiceCapture, Is.False);
    }

    // ── edge ──────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_NoIds_ReturnsEmptyAndNeverTouchesTheBus()
    {
        var bundle = PrivacyTestFactory.Build();

        var result = await bundle.Settings.GetAsync([" ", ""]);

        Assert.That(result, Is.Empty);
        await bundle.Bus.DidNotReceiveWithAnyArgs().InvokeAsync<GetUserPrivacySettingsResponse>(
            default!, default, default);
    }

    [Test]
    public async Task GetAsync_UnreadableCachedEntry_IsTreatedAsAMiss()
    {
        var cache = new FakeDistributedCache();
        cache.SetEntry(PrivacySettingsCache.KeyFor(UserId), "{ this is not the envelope shape ");
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, true)], cache: cache);

        var settings = await bundle.Settings.GetAsync(UserId);

        Assert.That(settings.AllowPositionalVoiceCapture, Is.True, "a corrupt entry must re-ask, not fail");
    }

    [Test]
    public async Task InvalidateAsync_DropsTheEntrySoTheNextReadReAsks()
    {
        var bundle = PrivacyTestFactory.Build([PrivacyTestFactory.WithPositionalVoice(UserId, true)]);
        await bundle.Settings.GetAsync(UserId);

        await bundle.Settings.InvalidateAsync(UserId);
        await bundle.Settings.GetAsync(UserId);

        Assert.That(bundle.Cache.HasEntry(PrivacySettingsCache.KeyFor(UserId)), Is.True);
        await bundle.Bus.Received(2).InvokeAsync<GetUserPrivacySettingsResponse>(
            Arg.Any<GetUserPrivacySettingsRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Test]
    public async Task InvalidateAsync_WhenRedisIsDown_DoesNotThrow()
    {
        // A failed eviction means the old value survives until the refresh window elapses. That is
        // worth a warning, not worth failing the whole message and retrying it.
        var cache = new FakeDistributedCache { Broken = true };
        var bundle = PrivacyTestFactory.Build(cache: cache);

        Assert.That(async () => await bundle.Settings.InvalidateAsync(UserId), Throws.Nothing);
    }

    // ── negative (the point) ──────────────────────────────────────────────

    [Test]
    public async Task GetAsync_IdentityUnreachableAndNothingCached_FallsBackToRestrictiveDefaults()
    {
        var bundle = PrivacyTestFactory.Build(lookupFails: true);

        var settings = await bundle.Settings.GetAsync(UserId);

        Assert.Multiple(() =>
        {
            Assert.That(settings.AllowPositionalVoiceCapture, Is.False);
            Assert.That(settings.ShareActivity, Is.False);
            Assert.That(settings.AllowVoiceRecordingInClips, Is.False);
        });
    }

    [Test]
    public async Task GetAsync_IdentityUnreachable_NeverCachesTheFallback()
    {
        var bundle = PrivacyTestFactory.Build(lookupFails: true);

        await bundle.Settings.GetAsync(UserId);

        Assert.That(bundle.Cache.HasEntry(PrivacySettingsCache.KeyFor(UserId)), Is.False,
            "a guess must not become the value the next reader trusts");
    }

    [Test]
    public async Task GetAsync_IdentityUnreachableButAStaleEntryExists_PrefersTheUsersOwnChoice()
    {
        // Past the refresh window, so the cache re-asks - and the re-ask fails. The last thing the
        // user actually chose is a better answer than a fabricated default, and turning a brief
        // Identity blip into "nobody may use voice" would be an outage dressed up as a control.
        var cache = new FakeDistributedCache();
        var stale = PrivacyTestFactory.WithPositionalVoice(UserId, true);
        cache.SetEntry(PrivacySettingsCache.KeyFor(UserId), JsonSerializer.Serialize(new
        {
            Value = stale,
            FetchedAt = DateTimeOffset.UtcNow - PrivacySettingsCache.RefreshAfter - TimeSpan.FromMinutes(1),
        }));
        var bundle = PrivacyTestFactory.Build(lookupFails: true, cache: cache);

        var settings = await bundle.Settings.GetAsync(UserId);

        Assert.That(settings.AllowPositionalVoiceCapture, Is.True);
    }

    [Test]
    public async Task GetAsync_IdentityAnswersWithoutTheRequestedUser_RefusesRatherThanAssuming()
    {
        // A user Identity does not know about is not an unrestricted user.
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<GetUserPrivacySettingsResponse>(
                Arg.Any<GetUserPrivacySettingsRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new GetUserPrivacySettingsResponse { Settings = [] });
        var cache = new FakeDistributedCache();
        var settingsCache = new PrivacySettingsCache(cache, bus,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PrivacySettingsCache>.Instance);

        var settings = await settingsCache.GetAsync(UserId);

        Assert.That(settings.AllowPositionalVoiceCapture, Is.False);
    }
}
