using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Integration.Relationship.Consumers;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Dtos;
using Social.Domain.Aggregate;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

[TestFixture]
public class GetProfilesByUserIdsHandlerTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);
        _cache = new FakeDistributedCache();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task Handle_AllUncached_QueriesDbAndWarmsCacheForEach()
    {
        var profileA = Profile.Create(new CreateProfileParams { UserId = "user-a", Username = "a" });
        var profileB = Profile.Create(new CreateProfileParams { UserId = "user-b", Username = "b" });
        _context.Profiles.AddRange(profileA, profileB);
        await _context.SaveChangesAsync();

        var response = await GetProfilesByUserIdsHandler.Handle(
            new GetProfileByUserIdsRequest { UserIds = ["user-a", "user-b"] },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.That(response.Profiles, Has.Count.EqualTo(2));
        Assert.That(_cache.HasEntry(ProfileDto.GetCacheIdByUserId("user-a")), Is.True);
        Assert.That(_cache.HasEntry(ProfileDto.GetCacheIdByUserId("user-b")), Is.True);
    }

    [Test]
    public async Task Handle_PartiallyCached_OnlyQueriesRemainingIds()
    {
        var cachedDto = new ProfileDto { Id = "prfl_a", UserId = "user-a", UserName = "cached-a" };
        _cache.SetEntry(ProfileDto.GetCacheIdByUserId("user-a"), JsonSerializer.Serialize(cachedDto));

        var profileB = Profile.Create(new CreateProfileParams { UserId = "user-b", Username = "b" });
        _context.Profiles.Add(profileB);
        await _context.SaveChangesAsync();

        var response = await GetProfilesByUserIdsHandler.Handle(
            new GetProfileByUserIdsRequest { UserIds = ["user-a", "user-b"] },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.That(response.Profiles, Has.Count.EqualTo(2));
        var fromCache = response.Profiles.Single(p => p.UserId == "user-a");
        Assert.That(fromCache.UserName, Is.EqualTo("cached-a"));
    }

    [Test]
    public async Task Handle_UnknownUserId_IsOmittedFromResponse()
    {
        var response = await GetProfilesByUserIdsHandler.Handle(
            new GetProfileByUserIdsRequest { UserIds = ["no-such-user"] },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.That(response.Profiles, Is.Empty);
    }
}
