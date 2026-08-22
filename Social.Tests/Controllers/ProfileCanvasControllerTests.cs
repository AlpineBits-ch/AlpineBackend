using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Controllers;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Controllers;

/// <summary>The canvas routes, their validation and the server-side visibility strip.</summary>
[TestFixture]
public class ProfileCanvasControllerTests
{
    private TestSocialContext _context = null!;
    private FakeSocialHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _hub = new FakeSocialHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private ProfileCanvasController MakeController(string? userId, ISharedGuildResolver? sharedGuilds = null)
    {
        var canvases = new ProfileCanvasService(_context, sharedGuilds ?? new NoSharedGuildResolver());
        var realtime = new ProfileCanvasRealtime(canvases, _hub, NullLogger<ProfileCanvasRealtime>.Instance);

        // Null S3 client: no test here reaches storage. The image cap refuses before the upload,
        // which is the ordering the ninth-upload test pins.
        var files = new FileService(null!, new MemoryCache(new MemoryCacheOptions()));

        var controller = new ProfileCanvasController(_context, canvases, realtime, files);
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

    private async Task Befriend(Profile a, Profile b)
    {
        _context.Relationships.AddRange(
            new Relationship { Id = $"rlsp_{a.Id}_{b.Id}", OwnerId = a.Id, TargetId = b.Id, Status = RelationshipStatus.Friends },
            new Relationship { Id = $"rlsp_{b.Id}_{a.Id}", OwnerId = b.Id, TargetId = a.Id, Status = RelationshipStatus.Friends });
        await _context.SaveChangesAsync();
    }

    private static JsonElement Config(string json) => JsonDocument.Parse(json).RootElement;

    private static CanvasWidgetDto Widget(string id, string visibility = "everyone", string config = "{}", double y = 0) => new()
    {
        Id = id, Type = "quote", X = 0, Y = y, W = 1, H = 1,
        Visibility = visibility, Card = false, Config = Config(config),
    };

    private static CanvasWriteDto Write(params CanvasWidgetDto[] widgets) =>
        new() { Theme = new CanvasThemeDto(), Widgets = widgets };

    private static ProfileCanvasDto Body(IActionResult result) =>
        (ProfileCanvasDto)((OkObjectResult)result).Value!;

    // ── Reads ────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetCanvas_on_a_profile_that_never_saved_one_is_a_404()
    {
        var profile = await AddProfile("user-1", "one");
        var controller = MakeController("user-1");

        var result = await controller.GetCanvasAsync(profile.Id, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetCanvas_without_a_caller_is_unauthorized()
    {
        var profile = await AddProfile("user-1", "one");
        var controller = MakeController(null);

        var result = await controller.GetCanvasAsync(profile.Id, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task GetCanvas_for_an_unknown_profile_is_a_404()
    {
        await AddProfile("user-1", "one");
        var controller = MakeController("user-1");

        var result = await controller.GetCanvasAsync("prfl_nope", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetCanvas_hides_the_canvas_from_a_blocked_reader()
    {
        var owner = await AddProfile("user-1", "one");
        var stranger = await AddProfile("user-2", "two");
        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_block", OwnerId = owner.Id, TargetId = stranger.Id, Status = RelationshipStatus.Blocked,
        });
        await _context.SaveChangesAsync();

        await MakeController("user-1").SaveCanvasAsync(Write(Widget("w1")), CancellationToken.None);

        var result = await MakeController("user-2").GetCanvasAsync(owner.Id, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    [Test]
    public async Task SaveCanvas_round_trips_config_byte_identically()
    {
        var owner = await AddProfile("user-1", "one");
        const string config = """{"lines":["a","b"],"nested":{"n":1,"flag":true,"missing":null},"n":0}""";

        await MakeController("user-1").SaveCanvasAsync(Write(Widget("w1", config: config)), CancellationToken.None);

        var read = await MakeController("user-1").GetCanvasAsync(owner.Id, CancellationToken.None);
        var stored = Body(read).Widgets.Single().Config;

        Assert.That(JsonSerializer.Serialize(stored, CanvasJson.Options), Is.EqualTo(config));
    }

    [Test]
    public async Task SaveCanvas_starts_at_version_one_and_increments()
    {
        await AddProfile("user-1", "one");

        var first = Body(await MakeController("user-1").SaveCanvasAsync(Write(Widget("w1")), CancellationToken.None));
        var second = Body(await MakeController("user-1").SaveCanvasAsync(Write(Widget("w1")), CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(first.Version, Is.EqualTo(1));
            Assert.That(second.Version, Is.EqualTo(2));
            Assert.That(second.UpdatedAt, Is.GreaterThanOrEqualTo(first.UpdatedAt));
        });
    }

    [Test]
    public async Task SaveCanvas_moves_updatedAt()
    {
        await AddProfile("user-1", "one");

        var first = Body(await MakeController("user-1").SaveCanvasAsync(Write(Widget("w1")), CancellationToken.None));
        await Task.Delay(50);
        var second = Body(await MakeController("user-1").SaveCanvasAsync(Write(Widget("w1")), CancellationToken.None));

        Assert.That(second.UpdatedAt, Is.GreaterThan(first.UpdatedAt));
    }

    [Test]
    public async Task SaveCanvas_writes_only_the_callers_own_canvas()
    {
        await AddProfile("user-1", "one");
        var other = await AddProfile("user-2", "two");

        await MakeController("user-1").SaveCanvasAsync(Write(Widget("w1")), CancellationToken.None);

        var otherCanvas = await MakeController("user-2").GetCanvasAsync(other.Id, CancellationToken.None);

        Assert.That(otherCanvas, Is.InstanceOf<NotFoundResult>(), "user-1's write must not land on another profile");
    }

    [Test]
    public async Task SaveCanvas_without_a_caller_is_unauthorized()
    {
        var result = await MakeController(null).SaveCanvasAsync(Write(Widget("w1")), CancellationToken.None);

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task SaveCanvas_rejects_an_invalid_body_with_the_field_named()
    {
        await AddProfile("user-1", "one");
        var widget = Widget("w1");
        widget.H = double.PositiveInfinity;

        var result = await MakeController("user-1").SaveCanvasAsync(Write(widget), CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That((string)((BadRequestObjectResult)result).Value!, Does.Contain("widgets[0].h"));
    }

    // ── Visibility ───────────────────────────────────────────────────────────

    [Test]
    public async Task A_friends_widget_is_absent_for_a_stranger_and_present_for_a_friend()
    {
        var owner = await AddProfile("user-1", "one");
        var friend = await AddProfile("user-2", "two");
        await AddProfile("user-3", "three");
        await Befriend(owner, friend);

        await MakeController("user-1").SaveCanvasAsync(
            Write(Widget("open"), Widget("mates", visibility: "friends", y: 1)), CancellationToken.None);

        var asFriend = Body(await MakeController("user-2").GetCanvasAsync(owner.Id, CancellationToken.None));
        var asStranger = Body(await MakeController("user-3").GetCanvasAsync(owner.Id, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(asFriend.Widgets.Select(w => w.Id), Is.EquivalentTo(new[] { "open", "mates" }));
            Assert.That(asStranger.Widgets.Select(w => w.Id), Is.EquivalentTo(new[] { "open" }));
        });
    }

    [Test]
    public async Task A_mutuals_widget_is_absent_for_a_non_mutual()
    {
        var owner = await AddProfile("user-1", "one");
        await AddProfile("user-2", "two");

        await MakeController("user-1").SaveCanvasAsync(
            Write(Widget("open"), Widget("shared", visibility: "mutuals", y: 1)), CancellationToken.None);

        var asStranger = Body(await MakeController("user-2").GetCanvasAsync(owner.Id, CancellationToken.None));

        Assert.That(asStranger.Widgets.Select(w => w.Id), Is.EquivalentTo(new[] { "open" }));
    }

    [Test]
    public async Task A_mutuals_widget_is_present_for_someone_sharing_a_guild()
    {
        var owner = await AddProfile("user-1", "one");
        await AddProfile("user-2", "two");

        await MakeController("user-1").SaveCanvasAsync(
            Write(Widget("open"), Widget("shared", visibility: "mutuals", y: 1)), CancellationToken.None);

        var controller = MakeController("user-2", new StubSharedGuildResolver("user-1", "user-2"));
        var asMutual = Body(await controller.GetCanvasAsync(owner.Id, CancellationToken.None));

        Assert.That(asMutual.Widgets.Select(w => w.Id), Is.EquivalentTo(new[] { "open", "shared" }));
    }

    [Test]
    public async Task A_mutuals_widget_is_present_for_someone_with_a_friend_in_common()
    {
        var owner = await AddProfile("user-1", "one");
        var viewer = await AddProfile("user-2", "two");
        var common = await AddProfile("user-3", "three");
        await Befriend(owner, common);
        await Befriend(viewer, common);

        await MakeController("user-1").SaveCanvasAsync(
            Write(Widget("shared", visibility: "mutuals")), CancellationToken.None);

        var asMutual = Body(await MakeController("user-2").GetCanvasAsync(owner.Id, CancellationToken.None));

        Assert.That(asMutual.Widgets.Select(w => w.Id), Is.EquivalentTo(new[] { "shared" }));
    }

    [Test]
    public async Task The_owner_sees_every_widget()
    {
        var owner = await AddProfile("user-1", "one");

        await MakeController("user-1").SaveCanvasAsync(
            Write(Widget("open"), Widget("mates", visibility: "friends", y: 1), Widget("shared", visibility: "mutuals", y: 2)),
            CancellationToken.None);

        var mine = Body(await MakeController("user-1").GetCanvasAsync(owner.Id, CancellationToken.None));

        Assert.That(mine.Widgets, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Stripping_leaves_the_surviving_coordinates_alone()
    {
        var owner = await AddProfile("user-1", "one");
        await AddProfile("user-2", "two");

        await MakeController("user-1").SaveCanvasAsync(
            Write(Widget("mates", visibility: "friends"), Widget("open", y: 3)), CancellationToken.None);

        var asStranger = Body(await MakeController("user-2").GetCanvasAsync(owner.Id, CancellationToken.None));

        Assert.That(asStranger.Widgets.Single().Y, Is.EqualTo(3));
    }

    // ── Images ───────────────────────────────────────────────────────────────

    [Test]
    public async Task The_ninth_image_upload_is_rejected()
    {
        var owner = await AddProfile("user-1", "one");
        for (var i = 0; i < ProfileCanvasValidator.MaxImagesPerProfile; i++)
            _context.ProfileCanvasImages.Add(ProfileCanvasImage.Create(owner.Id, "image/png", 10));
        await _context.SaveChangesAsync();

        var result = await MakeController("user-1").UploadCanvasImageAsync(PngFile(), CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That((string)((BadRequestObjectResult)result).Value!,
            Does.Contain(ProfileCanvasValidator.MaxImagesPerProfile.ToString()));
    }

    [Test]
    public async Task An_unsupported_image_type_is_rejected()
    {
        await AddProfile("user-1", "one");

        var result = await MakeController("user-1").UploadCanvasImageAsync(
            PngFile(contentType: "application/zip"), CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Deleting_an_image_the_caller_does_not_hold_is_a_no_op()
    {
        await AddProfile("user-1", "one");

        var result = await MakeController("user-1").DeleteCanvasImageAsync("cnvi_nope", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task An_unknown_canvas_image_is_a_404()
    {
        var result = await MakeController(null).GetCanvasImageAsync("cnvi_nope", CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    private static IFormFile PngFile(string contentType = "image/png")
    {
        var bytes = Encoding.UTF8.GetBytes("not really a png");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "pic.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }
}
