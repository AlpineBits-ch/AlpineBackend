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

    // ── T0-3 ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_NarrowsOnlyTheBlockedSubjectsInTheBatch()
    {
        var viewer = Profile.Create(new CreateProfileParams { UserId = "user-viewer", Username = "viewer" });
        var blocker = Profile.Create(new CreateProfileParams { UserId = "user-blocker", Username = "blocker" });
        var innocent = Profile.Create(new CreateProfileParams { UserId = "user-innocent", Username = "innocent" });
        blocker.Bio = "blocker bio";
        innocent.Bio = "innocent bio";
        _context.Profiles.AddRange(viewer, blocker, innocent);
        await _context.SaveChangesAsync();

        _context.Relationships.Add(new Relationship
        {
            Id = Relationship.GenerateId(),
            OwnerId = blocker.Id,
            TargetId = viewer.Id,
            Status = Social.Domain.Enums.RelationshipStatus.Blocked,
        });
        await _context.SaveChangesAsync();

        var response = await GetProfilesByUserIdsHandler.Handle(
            new GetProfileByUserIdsRequest
            {
                UserIds = ["user-blocker", "user-innocent"],
                ViewerUserId = "user-viewer",
            },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(response.Profiles.Single(p => p.UserId == "user-blocker").Bio, Is.Null);
            Assert.That(response.Profiles.Single(p => p.UserId == "user-innocent").Bio, Is.EqualTo("innocent bio"));
        });
    }

    [Test]
    public async Task Handle_NoViewer_LeavesEveryProfileUntouched()
    {
        var subject = Profile.Create(new CreateProfileParams { UserId = "user-a", Username = "a" });
        subject.Bio = "bio";
        _context.Profiles.Add(subject);
        await _context.SaveChangesAsync();

        var response = await GetProfilesByUserIdsHandler.Handle(
            new GetProfileByUserIdsRequest { UserIds = ["user-a"] },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.That(response.Profiles.Single().Bio, Is.EqualTo("bio"));
    }
}
