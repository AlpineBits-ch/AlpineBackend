using AppEnvironment;
using Federation.Application.Services;
using Federation.Tests.Helpers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Federation.Tests;

[TestFixture]
public class UserServiceTests
{
    private IDistributedCache _cache = null!;
    private FakeMessageBus _bus = null!;
    private UserService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        _cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _bus = new FakeMessageBus();
        _service = new UserService(_cache, _bus);
    }

    // ── GetFederatedUserId ───────────────────────────────────────────────────

    [Test]
    public void GetFederatedUserId_SuffixesLocalIdWithInstanceUrl()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://test.venta.gg";

        var result = _service.GetFederatedUserId("usr_local123");

        Assert.That(result, Is.EqualTo("usr_local123:https://test.venta.gg"));
    }

    [Test]
    public void GetFederatedUserId_DifferentInstanceUrl_ProducesDifferentSuffix()
    {
        Env.GeneralConfiguration.InstanceUrl = "https://other.venta.gg";

        var result = _service.GetFederatedUserId("usr_local123");

        Assert.That(result, Is.EqualTo("usr_local123:https://other.venta.gg"));
    }

    // ── GetUserProfile ───────────────────────────────────────────────────────

    [Test]
    public async Task GetUserProfile_CacheMiss_InvokesBusAndCachesResult()
    {
        _bus.ProfileResponse = new GetProfileByUserIdResponse
        {
            Profile = new ProfileDto { Id = "prf_1", UserId = "usr_1", UserName = "Alice" }
        };

        var result = await _service.GetUserProfile("usr_1");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UserName, Is.EqualTo("Alice"));
        Assert.That(_bus.Invoked, Has.Count.EqualTo(1));

        // Second call should now be served from cache - no additional bus call.
        var cached = await _cache.GetStringAsync("user_profile:usr_1");
        Assert.That(cached, Is.Not.Null);
    }

    [Test]
    public async Task GetUserProfile_CacheHit_DoesNotInvokeBus()
    {
        await _cache.SetStringAsync("user_profile:usr_2",
            System.Text.Json.JsonSerializer.Serialize(new ProfileDto { Id = "prf_2", UserId = "usr_2", UserName = "Bob" }));

        var result = await _service.GetUserProfile("usr_2");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UserName, Is.EqualTo("Bob"));
        Assert.That(_bus.Invoked, Is.Empty, "A cache hit must not fall through to the bus");
    }

    [Test]
    public async Task GetUserProfile_BusReturnsNoProfile_ReturnsNullAndDoesNotCache()
    {
        _bus.ProfileResponse = new GetProfileByUserIdResponse { Profile = null };

        var result = await _service.GetUserProfile("usr_missing");

        Assert.That(result, Is.Null);
        var cached = await _cache.GetStringAsync("user_profile:usr_missing");
        Assert.That(cached, Is.Null);
    }
}
