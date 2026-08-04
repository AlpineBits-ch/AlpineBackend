using System.Text.Json;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
using Social.Api.Services;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

/// <summary>
/// The fail-closed contract from privacy spec §1.4. The negative test - cache miss plus bus failure
/// yielding restrictive defaults - is the one the spec lists as an acceptance criterion.
/// </summary>
[TestFixture]
public class PrivacySettingsCacheTests
{
    private FakeDistributedCache _cache = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _bus = new FakeMessageBus();
    }

    [Test]
    public void KeyFor_UsesTheKeyTheSpecNames()
    {
        Assert.That(PrivacySettingsCache.KeyFor("usr_1"), Is.EqualTo("privacy_settings:user_id:usr_1"));
    }

    // ── normal ───────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_CacheMiss_FetchesOverTheBusAndCachesTheResult()
    {
        var settings = PrivacyTestHelpers.Defaults("usr_1");
        settings.FriendRequestPolicy = FriendRequestPolicy.FriendsOfFriends;
        var sut = PrivacyTestHelpers.CacheReturning(_cache, _bus, settings);

        var result = await sut.GetAsync("usr_1");

        Assert.Multiple(() =>
        {
            Assert.That(result.FriendRequestPolicy, Is.EqualTo(FriendRequestPolicy.FriendsOfFriends));
            Assert.That(_cache.HasEntry(PrivacySettingsCache.KeyFor("usr_1")), Is.True);
            Assert.That(_bus.LastInvoked, Is.InstanceOf<GetUserPrivacySettingsRequest>());
        });
    }

    [Test]
    public async Task GetAsync_CacheHit_DoesNotTouchTheBus()
    {
        var cached = PrivacyTestHelpers.Defaults("usr_1");
        cached.DiscoverableByUsername = false;
        _cache.SetEntry(PrivacySettingsCache.KeyFor("usr_1"), JsonSerializer.Serialize(cached));

        // No registered response: reaching the bus would throw, so this asserts the cache short-circuit.
        var sut = PrivacyTestHelpers.FailingCache(_cache, _bus);

        var result = await sut.GetAsync("usr_1");

        Assert.Multiple(() =>
        {
            Assert.That(result.DiscoverableByUsername, Is.False);
            Assert.That(_bus.LastInvoked, Is.Null);
        });
    }

    [Test]
    public async Task GetManyAsync_MixesCachedAndFetchedIdsInOneCall()
    {
        var cached = PrivacyTestHelpers.Defaults("usr_cached");
        _cache.SetEntry(PrivacySettingsCache.KeyFor("usr_cached"), JsonSerializer.Serialize(cached));

        var fetched = PrivacyTestHelpers.Defaults("usr_fetched");
        var sut = PrivacyTestHelpers.CacheReturning(_cache, _bus, fetched);

        var result = await sut.GetManyAsync(["usr_cached", "usr_fetched"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Keys, Is.EquivalentTo(new[] { "usr_cached", "usr_fetched" }));
            Assert.That(((GetUserPrivacySettingsRequest)_bus.LastInvoked!).UserIds, Is.EquivalentTo(new[] { "usr_fetched" }),
                "already-cached ids must not be re-requested");
        });
    }

    // ── fail closed ──────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_CacheMissAndBusFailure_YieldsRestrictiveDefaults()
    {
        var sut = PrivacyTestHelpers.FailingCache(_cache, _bus);

        var result = await sut.GetAsync("usr_1");

        Assert.Multiple(() =>
        {
            Assert.That(result.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Friends));
            Assert.That(result.FriendRequestPolicy, Is.EqualTo(FriendRequestPolicy.Nobody));
            Assert.That(result.DiscoverableByUsername, Is.False);
            Assert.That(result.DiscoverableByEmail, Is.False);
            Assert.That(result.DiscoverableByPhone, Is.False);
            Assert.That(result.MutualServersVisibility, Is.EqualTo(Visibility.Nobody));
            Assert.That(result.MutualFriendsVisibility, Is.EqualTo(Visibility.Nobody));
            Assert.That(result.ConnectionsVisibility, Is.EqualTo(Visibility.Nobody));
            Assert.That(result.BirthdayVisibility, Is.EqualTo(Visibility.Nobody));
            Assert.That(result.ShareActivity, Is.False);
            Assert.That(result.AllowDataCollection, Is.False);
            Assert.That(result.HidePushContent, Is.True);
        });
    }

    [Test]
    public async Task GetAsync_Failure_DoesNotCacheTheFallback()
    {
        // A cached fallback would be indistinguishable from a real read on the next request, and
        // would keep the user restricted for the whole TTL after the outage ended.
        var sut = PrivacyTestHelpers.FailingCache(_cache, _bus);

        await sut.GetAsync("usr_1");

        Assert.That(_cache.HasEntry(PrivacySettingsCache.KeyFor("usr_1")), Is.False);
    }

    [Test]
    public async Task GetManyAsync_IdIdentityDoesNotAnswerFor_GetsRestrictiveDefaults()
    {
        var known = PrivacyTestHelpers.Defaults("usr_known");
        var sut = PrivacyTestHelpers.CacheReturning(_cache, _bus, known);

        var result = await sut.GetManyAsync(["usr_known", "usr_unknown"]);

        Assert.Multiple(() =>
        {
            Assert.That(result["usr_known"].FriendRequestPolicy, Is.EqualTo(FriendRequestPolicy.Everyone));
            Assert.That(result["usr_unknown"].FriendRequestPolicy, Is.EqualTo(FriendRequestPolicy.Nobody));
        });
    }

    [Test]
    public async Task GetAsync_PoisonedCacheEntry_FallsBackToTheBusRatherThanThrowing()
    {
        _cache.SetEntry(PrivacySettingsCache.KeyFor("usr_1"), "{ not json");
        var sut = PrivacyTestHelpers.CacheReturning(_cache, _bus, PrivacyTestHelpers.Defaults("usr_1"));

        var result = await sut.GetAsync("usr_1");

        Assert.That(result.FriendRequestPolicy, Is.EqualTo(FriendRequestPolicy.Everyone));
    }

    // ── eviction ─────────────────────────────────────────────────────────────

    [Test]
    public async Task EvictAsync_DropsTheKey()
    {
        _cache.SetEntry(PrivacySettingsCache.KeyFor("usr_1"),
            JsonSerializer.Serialize(PrivacyTestHelpers.Defaults("usr_1")));
        var sut = PrivacyTestHelpers.FailingCache(_cache, _bus);

        await sut.EvictAsync("usr_1");

        Assert.That(_cache.HasEntry(PrivacySettingsCache.KeyFor("usr_1")), Is.False);
    }

    [Test]
    public async Task GetManyAsync_NoIds_ReturnsEmptyWithoutTouchingTheBus()
    {
        var sut = PrivacyTestHelpers.FailingCache(_cache, _bus);

        var result = await sut.GetManyAsync([]);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Empty);
            Assert.That(_bus.LastInvoked, Is.Null);
        });
    }
}
