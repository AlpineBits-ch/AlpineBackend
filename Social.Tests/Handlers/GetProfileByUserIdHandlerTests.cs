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

    // ── T0-3: the bus route must not be a way around the block ───────────────

    private async Task<(Profile Viewer, Profile Subject)> SeedBlockedPairAsync()
    {
        var viewer = Profile.Create(new CreateProfileParams { UserId = "user-viewer", Username = "viewer" });
        var subject = Profile.Create(new CreateProfileParams { UserId = "user-subject", Username = "subject" });
        subject.Bio = "private bio";
        _context.Profiles.AddRange(viewer, subject);
        await _context.SaveChangesAsync();

        _context.Relationships.Add(new Relationship
        {
            Id = Relationship.GenerateId(),
            OwnerId = subject.Id,
            TargetId = viewer.Id,
            Status = Social.Domain.Enums.RelationshipStatus.Blocked,
        });
        await _context.SaveChangesAsync();

        return (viewer, subject);
    }

    [Test]
    public async Task Handle_BlockedViewer_GetsTheMinimalProjection()
    {
        await SeedBlockedPairAsync();

        var response = await GetProfileByUserIdHandler.Handle(
            new GetProfileByUserIdRequest { UserId = "user-subject", ViewerUserId = "user-viewer" },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(response.Profile!.UserName, Is.EqualTo("subject"));
            Assert.That(response.Profile!.Bio, Is.Null);
            Assert.That(response.Profile!.Relationships, Is.Empty);
        });
    }

    [Test]
    public async Task Handle_UnrelatedViewer_GetsTheOrdinaryProjection()
    {
        await SeedBlockedPairAsync();
        var other = Profile.Create(new CreateProfileParams { UserId = "user-other", Username = "other" });
        _context.Profiles.Add(other);
        await _context.SaveChangesAsync();

        var response = await GetProfileByUserIdHandler.Handle(
            new GetProfileByUserIdRequest { UserId = "user-subject", ViewerUserId = "user-other" },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.That(response.Profile!.Bio, Is.EqualTo("private bio"));
    }

    [Test]
    public async Task Handle_BlockedViewer_LeavesTheSharedCacheEntryUnfiltered()
    {
        // Regression guard for the obvious way to get this wrong: scrubbing the cached instance in
        // place would serve the blocked reader's narrowed view to everybody else too.
        await SeedBlockedPairAsync();

        await GetProfileByUserIdHandler.Handle(
            new GetProfileByUserIdRequest { UserId = "user-subject", ViewerUserId = "user-viewer" },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        var cachedBytes = _cache.Get("integration_profile:user_id:user-subject");
        var cached = JsonSerializer.Deserialize<ProfileDto>(cachedBytes!);
        Assert.That(cached!.Bio, Is.EqualTo("private bio"));
    }

    [Test]
    public async Task Handle_NoViewerSupplied_StillReturnsTheSubjectView()
    {
        // Additive: an existing caller that never sets ViewerUserId keeps working unchanged.
        await SeedBlockedPairAsync();

        var response = await GetProfileByUserIdHandler.Handle(
            new GetProfileByUserIdRequest { UserId = "user-subject" },
            NullLogger<GetProfileByUserIdHandler>.Instance, _context, _cache);

        Assert.That(response.Profile!.Bio, Is.EqualTo("private bio"));
    }
}
