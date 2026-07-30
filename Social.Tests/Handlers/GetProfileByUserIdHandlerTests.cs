using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Integration.Relationship.Consumers;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Dtos;
using Social.Domain.Aggregate;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

[TestFixture]
public class GetProfileByUserIdHandlerTests
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
    public async Task Handle_CacheHit_ReturnsCachedProfileWithoutHittingDb()
    {
        var cached = new ProfileDto { Id = "prfl_cached", UserId = "user-1", UserName = "cached-user" };
        _cache.SetEntry("integration_profile:user_id:user-1", JsonSerializer.Serialize(cached));

        var response = await GetProfileByUserIdHandler.Handle(
            new GetProfileByUserIdRequest { UserId = "user-1" },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.That(response.Profile, Is.Not.Null);
        Assert.That(response.Profile!.Id, Is.EqualTo("prfl_cached"));
    }

    [Test]
    public async Task Handle_CacheMissAndProfileExists_ReturnsProfileAndPopulatesCache()
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = "user-2", Username = "tester" });
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();

        var response = await GetProfileByUserIdHandler.Handle(
            new GetProfileByUserIdRequest { UserId = "user-2" },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.That(response.Profile, Is.Not.Null);
        Assert.That(response.Profile!.UserId, Is.EqualTo("user-2"));
        Assert.That(_cache.HasEntry("integration_profile:user_id:user-2"), Is.True, "handler must warm the cache after a DB read");
    }

    [Test]
    public async Task Handle_CacheMissAndProfileDoesNotExist_ReturnsEmptyResponse()
    {
        var response = await GetProfileByUserIdHandler.Handle(
            new GetProfileByUserIdRequest { UserId = "no-such-user" },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.That(response.Profile, Is.Null);
    }
}
