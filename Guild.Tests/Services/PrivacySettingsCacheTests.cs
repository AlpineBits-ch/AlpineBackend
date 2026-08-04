using System.Text.Json;
using Guild.Application.Services;
using Guild.Tests.Helpers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;

namespace Guild.Tests.Services;

/// <summary>
/// Covers <see cref="PrivacySettingsCache"/>.
///
/// <para>The negative cases are the point. A privacy cache that answers permissively when it cannot
/// reach its source is worse than no cache, because every enforcement point downstream then quietly
/// stops enforcing at exactly the moment nobody is watching.</para>
/// </summary>
[TestFixture]
public class PrivacySettingsCacheTests
{
    private const string UserId = "user-1";
    private const string OtherUserId = "user-2";

    private FakeDistributedCache _cache = null!;
    private FakeInvokingMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _bus = new FakeInvokingMessageBus();
    }

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Get_OnAMiss_AsksIdentityAndReturnsWhatItSaid()
    {
        var stored = PrivacyTestFactory.Permissive(UserId);
        stored.HidePushContent = true;

        var sut = PrivacyTestFactory.Privacy(_bus, _cache, stored);

        var settings = await sut.GetAsync(UserId);

        Assert.Multiple(() =>
        {
            Assert.That(settings.HidePushContent, Is.True);
            Assert.That(settings.SendTypingIndicators, Is.True);
            Assert.That(_bus.Invoked.OfType<GetUserPrivacySettingsRequest>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Get_WritesTheAnswerUnderThePrefixedKey_SoASecondReadIsFree()
    {
        var sut = PrivacyTestFactory.Privacy(_bus, _cache, PrivacyTestFactory.Permissive(UserId));

        await sut.GetAsync(UserId);
        await sut.GetAsync(UserId);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry($"privacy_settings:user_id:{UserId}"), Is.True);
            // Second call served from Redis - one bus round trip, not two.
            Assert.That(_bus.Invoked.OfType<GetUserPrivacySettingsRequest>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Get_ManyUsers_IsOneBusCallForTheWholeBatch()
    {
        var sut = PrivacyTestFactory.Privacy(_bus, _cache,
            PrivacyTestFactory.Permissive(UserId), PrivacyTestFactory.Permissive(OtherUserId));

        var settings = await sut.GetAsync([UserId, OtherUserId]);

        Assert.Multiple(() =>
        {
            Assert.That(settings, Has.Count.EqualTo(2));
            Assert.That(_bus.Invoked.OfType<GetUserPrivacySettingsRequest>().Count(), Is.EqualTo(1));
        });
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Get_MixedHitAndMiss_OnlyAsksAboutTheMiss()
    {
        _cache.SetEntry($"privacy_settings:user_id:{UserId}",
            JsonSerializer.Serialize(PrivacyTestFactory.Permissive(UserId)));

        var sut = PrivacyTestFactory.Privacy(_bus, _cache, PrivacyTestFactory.Permissive(OtherUserId));

        await sut.GetAsync([UserId, OtherUserId]);

        var request = _bus.Invoked.OfType<GetUserPrivacySettingsRequest>().Single();
        Assert.That(request.UserIds, Is.EquivalentTo(new[] { OtherUserId }));
    }

    [Test]
    public async Task Get_EmptyInput_TouchesNeitherRedisNorTheBus()
    {
        var sut = PrivacyTestFactory.Privacy(_bus, _cache);

        var settings = await sut.GetAsync([]);

        Assert.Multiple(() =>
        {
            Assert.That(settings, Is.Empty);
            Assert.That(_bus.Invoked, Is.Empty);
        });
    }

    [Test]
    public async Task Invalidate_DropsTheEntry_SoTheNextReadReachesIdentityAgain()
    {
        var sut = PrivacyTestFactory.Privacy(_bus, _cache, PrivacyTestFactory.Permissive(UserId));

        await sut.GetAsync(UserId);
        await sut.InvalidateAsync(UserId);
        await sut.GetAsync(UserId);

        Assert.That(_bus.Invoked.OfType<GetUserPrivacySettingsRequest>().Count(), Is.EqualTo(2));
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Get_CacheMissAndIdentityUnreachable_YieldsTheRestrictiveDefaults()
    {
        var sut = PrivacyTestFactory.UnreachablePrivacy(_bus, _cache);

        var settings = await sut.GetAsync(UserId);

        Assert.Multiple(() =>
        {
            // Never Everyone on error - the cross-cutting rule this whole class exists to hold.
            Assert.That(settings.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Friends));
            Assert.That(settings.FriendRequestPolicy, Is.EqualTo(FriendRequestPolicy.Nobody));
            Assert.That(settings.MutualServersVisibility, Is.EqualTo(Visibility.Nobody));
            Assert.That(settings.SendTypingIndicators, Is.False);
            Assert.That(settings.SendReadReceipts, Is.False);
            Assert.That(settings.ShareActivity, Is.False);
            Assert.That(settings.HidePushContent, Is.True);
            Assert.That(settings.AllowDataCollection, Is.False);
        });
    }

    [Test]
    public async Task Get_WhenIdentityIsUnreachable_DoesNotCacheTheFallback()
    {
        // Otherwise a momentary outage would pin every affected user to the restrictive defaults
        // for the whole TTL, long after Identity came back.
        var sut = PrivacyTestFactory.UnreachablePrivacy(_bus, _cache);

        await sut.GetAsync(UserId);

        Assert.That(_cache.HasEntry($"privacy_settings:user_id:{UserId}"), Is.False);
    }

    [Test]
    public async Task Get_IdentityAnswersButOmitsAUser_TreatsThatUserAsRestricted()
    {
        // "No answer" must never read as "no restriction", including a partial answer.
        _bus.SetResponse<GetUserPrivacySettingsRequest>(new GetUserPrivacySettingsResponse
        {
            Settings = [PrivacyTestFactory.Permissive(UserId)],
        });

        var sut = new PrivacySettingsCache(_cache, _bus,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PrivacySettingsCache>.Instance);

        var settings = await sut.GetAsync([UserId, OtherUserId]);

        Assert.Multiple(() =>
        {
            Assert.That(settings[UserId].SendTypingIndicators, Is.True);
            Assert.That(settings[OtherUserId].SendTypingIndicators, Is.False);
            Assert.That(settings[OtherUserId].HidePushContent, Is.True);
        });
    }

    [Test]
    public async Task Get_CorruptCacheEntry_FallsThroughToTheBusRatherThanThrowing()
    {
        _cache.SetEntry($"privacy_settings:user_id:{UserId}", "{ not json");

        var sut = PrivacyTestFactory.Privacy(_bus, _cache, PrivacyTestFactory.Permissive(UserId));

        var settings = await sut.GetAsync(UserId);

        Assert.That(settings.SendTypingIndicators, Is.True);
    }
}
