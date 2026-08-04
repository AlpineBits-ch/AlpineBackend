using System.Text.Json;
using Identity.Contracts.Bus.Events;
using Social.Api.Integration.Privacy;
using Social.Api.Services;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

/// <summary>
/// Privacy spec §1.5: "UserPrivacySettingsChangedEvent evicts the cache in every consuming service".
/// Without this the TTL decides when a user's toggle takes effect, which for a control whose whole
/// purpose is to stop something happening now is the same as not having it.
/// </summary>
[TestFixture]
public class UserPrivacySettingsChangedHandlerTests
{
    private FakeDistributedCache _cache = null!;
    private FakeMessageBus _bus = null!;
    private PrivacySettingsCache _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _bus = new FakeMessageBus();
        _sut = PrivacyTestHelpers.FailingCache(_cache, _bus);
    }

    [Test]
    public async Task Handle_EvictsTheChangedUsersKey()
    {
        _cache.SetEntry(PrivacySettingsCache.KeyFor("usr_1"),
            JsonSerializer.Serialize(PrivacyTestHelpers.Defaults("usr_1")));

        await UserPrivacySettingsChangedHandler.Handle(
            new UserPrivacySettingsChangedEvent { UserId = "usr_1", Version = 2 }, _sut);

        Assert.That(_cache.HasEntry(PrivacySettingsCache.KeyFor("usr_1")), Is.False);
    }

    [Test]
    public async Task Handle_LeavesOtherUsersKeysAlone()
    {
        _cache.SetEntry(PrivacySettingsCache.KeyFor("usr_1"), "a");
        _cache.SetEntry(PrivacySettingsCache.KeyFor("usr_2"), "b");

        await UserPrivacySettingsChangedHandler.Handle(
            new UserPrivacySettingsChangedEvent { UserId = "usr_1", Version = 2 }, _sut);

        Assert.That(_cache.HasEntry(PrivacySettingsCache.KeyFor("usr_2")), Is.True);
    }

    [Test]
    public async Task Handle_UncachedUser_IsANoOp()
    {
        Assert.DoesNotThrowAsync(() => UserPrivacySettingsChangedHandler.Handle(
            new UserPrivacySettingsChangedEvent { UserId = "usr_never_read", Version = 1 }, _sut));

        await Task.CompletedTask;
    }
}
