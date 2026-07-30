using System.Text.Json;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Social.Contracts.Services;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

[TestFixture]
public class ProfileServiceTests
{
    private FakeMessageBus _bus = null!;
    private FakeDistributedCache _cache = null!;
    private ProfileService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new FakeMessageBus();
        _cache = new FakeDistributedCache();
        _service = new ProfileService(_bus, _cache);
    }

    [Test]
    public async Task GetProfileByUserId_CacheHit_ReturnsCachedValueWithoutInvokingBus()
    {
        var cached = new ProfileDto { Id = "prfl_1", UserId = "user-1", UserName = "cached" };
        _cache.SetEntry(ProfileDto.GetCacheIdByUserId("user-1"), JsonSerializer.Serialize(cached));

        var result = await _service.GetProfileByUserId("user-1");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UserName, Is.EqualTo("cached"));
        Assert.That(_bus.LastInvoked, Is.Null, "must not hit the bus on a cache hit");
    }

    [Test]
    public async Task GetProfileByUserId_CacheMiss_InvokesBusAndCachesResult()
    {
        var busResponse = new GetProfileByUserIdResponse
        {
            Profile = new ProfileDto { Id = "prfl_2", UserId = "user-2", UserName = "from-bus" },
        };
        _bus.RespondWith<GetProfileByUserIdRequest, GetProfileByUserIdResponse>(busResponse);

        var result = await _service.GetProfileByUserId("user-2");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.UserName, Is.EqualTo("from-bus"));
        Assert.That(_cache.HasEntry(ProfileDto.GetCacheIdByUserId("user-2")), Is.True);
    }

    [Test]
    public async Task GetProfileByUserId_BusReturnsNullProfile_DoesNotCacheAndReturnsNull()
    {
        _bus.RespondWith<GetProfileByUserIdRequest, GetProfileByUserIdResponse>(new GetProfileByUserIdResponse { Profile = null });

        var result = await _service.GetProfileByUserId("user-3");

        Assert.That(result, Is.Null);
        Assert.That(_cache.HasEntry(ProfileDto.GetCacheIdByUserId("user-3")), Is.False);
    }

    [Test]
    public async Task GetProfilesByUserIds_AlwaysInvokesBus_ReturnsResponseProfiles()
    {
        var profiles = new List<ProfileDto>
        {
            new() { Id = "prfl_a", UserId = "user-a", UserName = "a" },
            new() { Id = "prfl_b", UserId = "user-b", UserName = "b" },
        };
        _bus.RespondWith<GetProfileByUserIdsRequest, GetProfileByUserIdsResponse>(
            new GetProfileByUserIdsResponse { Profiles = profiles });

        var result = await _service.GetProfilesByUserIds(["user-a", "user-b"]);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(_bus.LastInvoked, Is.InstanceOf<GetProfileByUserIdsRequest>());
    }
}
