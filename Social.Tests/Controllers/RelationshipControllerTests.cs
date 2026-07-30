using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Social.Api.Controllers;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Controllers;

[TestFixture]
public class RelationshipControllerTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private RelationshipController MakeController(string? userId)
    {
        var controller = new RelationshipController(_context);
        var principal = userId is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
        return controller;
    }

    private async Task<Profile> AddProfile(string userId, string userName)
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = userId, Username = userName });
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();
        return profile;
    }

    [Test]
    public async Task GetRelationships_NoUser_ReturnsBadRequest()
    {
        var controller = MakeController(null);

        var result = await controller.GetRelationships();

        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task GetRelationships_NoProfile_ReturnsBadRequest()
    {
        var controller = MakeController("no-such-user");

        var result = await controller.GetRelationships();

        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task GetRelationships_ExcludesNoneStatusRelationships()
    {
        var owner = await AddProfile("user-1", "owner");
        var target = await AddProfile("user-2", "target");
        _context.Relationships.AddRange(
            new Relationship { Id = "rlsp_1", OwnerId = owner.Id, TargetId = target.Id, Status = RelationshipStatus.Friends },
            new Relationship { Id = "rlsp_2", OwnerId = owner.Id, TargetId = target.Id, Status = RelationshipStatus.None });
        await _context.SaveChangesAsync();
        var controller = MakeController("user-1");

        var result = await controller.GetRelationships();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var relationships = (IEnumerable<RelationshipDto>)((OkObjectResult)result).Value!;
        Assert.That(relationships.Select(r => r.Id), Is.EquivalentTo(new[] { "rlsp_1" }));
    }

    [Test]
    public async Task GetRelationships_OnlyReturnsRelationshipsOwnedByCurrentProfile()
    {
        var owner = await AddProfile("user-1", "owner");
        var other = await AddProfile("user-2", "other");
        var thirdParty = await AddProfile("user-3", "third");
        _context.Relationships.AddRange(
            new Relationship { Id = "rlsp_mine", OwnerId = owner.Id, TargetId = other.Id, Status = RelationshipStatus.Friends },
            new Relationship { Id = "rlsp_not_mine", OwnerId = other.Id, TargetId = thirdParty.Id, Status = RelationshipStatus.Friends });
        await _context.SaveChangesAsync();
        var controller = MakeController("user-1");

        var result = await controller.GetRelationships();

        var relationships = (IEnumerable<RelationshipDto>)((OkObjectResult)result).Value!;
        Assert.That(relationships.Select(r => r.Id), Is.EquivalentTo(new[] { "rlsp_mine" }));
    }

    [Test]
    public async Task GetRelationships_NoRelationships_ReturnsEmptyList()
    {
        await AddProfile("user-1", "owner");
        var controller = MakeController("user-1");

        var result = await controller.GetRelationships();

        var relationships = (IEnumerable<RelationshipDto>)((OkObjectResult)result).Value!;
        Assert.That(relationships, Is.Empty);
    }
}
