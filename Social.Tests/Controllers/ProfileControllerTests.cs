using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Controllers;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Api.Services;
using Social.Contracts.Bus.Integration.Events;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Controllers;

[TestFixture]
public class ProfileControllerTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;
    private FakeMessageBus _bus = null!;
    private FakeDistributedCache _cache = null!;
    private ProfileProjectionService _projection = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);
        _bus = new FakeMessageBus();
        _cache = new FakeDistributedCache();
        // Shipped defaults for the users these tests seed; the privacy-specific behaviour lives in
        // ProfileControllerPrivacyTests, which registers its own settings.
        _projection = new ProfileProjectionService(
            _context,
            PrivacyTestHelpers.CacheReturning(_cache, _bus,
                PrivacyTestHelpers.Defaults("user-1"), PrivacyTestHelpers.Defaults("user-2")),
            new NoSharedGuildResolver(),
            new NoIdentityProfileFactsResolver());
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private ProfileController MakeController(string? userId)
    {
        var controller = new ProfileController(_context, NullLogger<ProfileController>.Instance, _bus, _projection,
            TestGuards.OfflineActivityGuard(_context), new FakeDistributedCache());
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

    // ── SetStatusAsync ───────────────────────────────────────────────────────

    [Test]
    public async Task SetStatusAsync_NoUser_ReturnsUnauthorized()
    {
        var controller = MakeController(null);

        var result = await controller.SetStatusAsync(new SetStatusDto { Status = "Online" });

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [TestCase("bogus-status")]
    [TestCase("Offline")] // Offline is a valid enum value but not client-settable
    public async Task SetStatusAsync_InvalidOrNonSettableStatus_ReturnsBadRequest(string status)
    {
        await AddProfile("user-1", "tester");
        var controller = MakeController("user-1");

        var result = await controller.SetStatusAsync(new SetStatusDto { Status = status });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task SetStatusAsync_NoProfile_ReturnsNotFound()
    {
        var controller = MakeController("no-such-user");

        var result = await controller.SetStatusAsync(new SetStatusDto { Status = "Online" });

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [TestCase("Online", OnlineStatus.Online)]
    [TestCase("Idle", OnlineStatus.Idle)]
    [TestCase("DoNotDisturb", OnlineStatus.DoNotDisturb)]
    [TestCase("Hidden", OnlineStatus.Hidden)]
    public async Task SetStatusAsync_ValidSettableStatus_UpdatesProfileAndPublishesEvent(string status, OnlineStatus expected)
    {
        await AddProfile("user-1", "tester");
        var controller = MakeController("user-1");

        var result = await controller.SetStatusAsync(new SetStatusDto { Status = status });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var stored = _context.Profiles.Single(p => p.UserId == "user-1");
        Assert.That(stored.OnlineStatus, Is.EqualTo(expected));

        var published = _bus.Published.OfType<UserStatusChanged>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(published.UserId, Is.EqualTo("user-1"));
            Assert.That(published.Status, Is.EqualTo(status));
        });
    }

    // ── UpdateProfileAsync ───────────────────────────────────────────────────

    [Test]
    public async Task UpdateProfileAsync_NoUser_ReturnsUnauthorized()
    {
        var controller = MakeController(null);

        var result = await controller.UpdateProfileAsync(new UpdateProfileDto());

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [TestCase("not-a-hex-color")]
    [TestCase("#ZZZZZZ")]
    [TestCase("#FFF")]
    public async Task UpdateProfileAsync_InvalidAccentColor_ReturnsBadRequest(string accentColor)
    {
        await AddProfile("user-1", "tester");
        var controller = MakeController("user-1");

        var result = await controller.UpdateProfileAsync(new UpdateProfileDto { AccentColor = accentColor });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateProfileAsync_InvalidFont_ReturnsBadRequest()
    {
        await AddProfile("user-1", "tester");
        var controller = MakeController("user-1");

        var result = await controller.UpdateProfileAsync(new UpdateProfileDto { Font = "NotAFont" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateProfileAsync_NoProfile_ReturnsNotFound()
    {
        var controller = MakeController("no-such-user");

        var result = await controller.UpdateProfileAsync(new UpdateProfileDto { Bio = "hi" });

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task UpdateProfileAsync_EmptyStringBio_ClearsBio()
    {
        var profile = await AddProfile("user-1", "tester");
        profile.Bio = "existing bio";
        await _context.SaveChangesAsync();
        var controller = MakeController("user-1");

        var result = await controller.UpdateProfileAsync(new UpdateProfileDto { Bio = "" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(_context.Profiles.Single(p => p.UserId == "user-1").Bio, Is.Null);
    }

    [Test]
    public async Task UpdateProfileAsync_NullFieldsLeaveExistingValuesUnchanged()
    {
        var profile = await AddProfile("user-1", "tester");
        profile.Bio = "existing bio";
        profile.AccentColor = "#123456";
        profile.Font = ProfileFont.Serif;
        await _context.SaveChangesAsync();
        var controller = MakeController("user-1");

        var result = await controller.UpdateProfileAsync(new UpdateProfileDto());

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var stored = _context.Profiles.Single(p => p.UserId == "user-1");
        Assert.Multiple(() =>
        {
            Assert.That(stored.Bio, Is.EqualTo("existing bio"));
            Assert.That(stored.AccentColor, Is.EqualTo("#123456"));
            Assert.That(stored.Font, Is.EqualTo(ProfileFont.Serif));
        });
    }

    [Test]
    public async Task UpdateProfileAsync_ValidValues_UpdatesAllFields()
    {
        await AddProfile("user-1", "tester");
        var controller = MakeController("user-1");

        var result = await controller.UpdateProfileAsync(new UpdateProfileDto
        {
            Bio = "new bio",
            AccentColor = "#5865F2",
            Font = "Monospace",
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var stored = _context.Profiles.Single(p => p.UserId == "user-1");
        Assert.Multiple(() =>
        {
            Assert.That(stored.Bio, Is.EqualTo("new bio"));
            Assert.That(stored.AccentColor, Is.EqualTo("#5865F2"));
            Assert.That(stored.Font, Is.EqualTo(ProfileFont.Monospace));
        });
    }

    // ── SelfAsync ────────────────────────────────────────────────────────────

    [Test]
    public async Task SelfAsync_NoUser_ReturnsNotFound()
    {
        var controller = MakeController(null);

        var result = await controller.SelfAsync();

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task SelfAsync_NoProfile_ReturnsNotFound()
    {
        var controller = MakeController("no-such-user");

        var result = await controller.SelfAsync();

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task SelfAsync_ExistingProfile_ReturnsOkWithDto()
    {
        await AddProfile("user-1", "tester");
        var controller = MakeController("user-1");

        var result = await controller.SelfAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (ProfileDto)((OkObjectResult)result).Value!;
        Assert.That(dto.UserId, Is.EqualTo("user-1"));
    }

    // ── GetAsync ─────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_NoUser_ReturnsNotFound()
    {
        var controller = MakeController(null);

        var result = await controller.GetAsync("some-id");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetAsync_CurrentProfileMissing_ReturnsNotFoundWithMessage()
    {
        var controller = MakeController("no-such-user");

        var result = await controller.GetAsync("some-id");

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetAsync_TargetProfileMissing_ReturnsNotFound()
    {
        await AddProfile("user-1", "tester");
        var controller = MakeController("user-1");

        var result = await controller.GetAsync("does-not-exist");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetAsync_TargetProfileExists_ReturnsOkWithDto()
    {
        await AddProfile("user-1", "tester");
        var target = await AddProfile("user-2", "target");
        var controller = MakeController("user-1");

        var result = await controller.GetAsync(target.Id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (ProfileDto)((OkObjectResult)result).Value!;
        Assert.That(dto.Id, Is.EqualTo(target.Id));
    }

    // ── GetByUserIdAsync ─────────────────────────────────────────────────────

    [Test]
    public async Task GetByUserIdAsync_NoUser_ReturnsNotFound()
    {
        var controller = MakeController(null);

        var result = await controller.GetByUserIdAsync("some-user");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetByUserIdAsync_TargetProfileMissing_ReturnsNotFound()
    {
        await AddProfile("user-1", "tester");
        var controller = MakeController("user-1");

        var result = await controller.GetByUserIdAsync("no-such-target");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetByUserIdAsync_TargetProfileExists_ReturnsOkWithDto()
    {
        await AddProfile("user-1", "tester");
        await AddProfile("user-2", "target");
        var controller = MakeController("user-1");

        var result = await controller.GetByUserIdAsync("user-2");

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (ProfileDto)((OkObjectResult)result).Value!;
        Assert.That(dto.UserId, Is.EqualTo("user-2"));
    }
}
