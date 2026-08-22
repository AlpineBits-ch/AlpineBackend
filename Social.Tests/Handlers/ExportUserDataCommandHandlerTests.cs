using System.Text.Json;
using Identity.Contracts.Bus.Commands;
using Social.Api.Commands;
using Social.Api.Dtos.Response;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

/// <summary>Social's participant in the export fan-out (T1-7).</summary>
[TestFixture]
public class ExportUserDataCommandHandlerTests
{
    private TestSocialContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestSocialContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private Task<Identity.Contracts.Bus.Response.ExportUserDataResponse> ExportAsync(string userId) =>
        ExportUserDataCommandHandler.Handle(
            new ExportUserDataCommand { ExportId = "dxrq_test", UserId = userId }, _context);

    private async Task<Profile> SeedProfileAsync(string userId, string username)
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = userId, Username = username });
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();
        return profile;
    }

    private static JsonElement Config(string json) => JsonDocument.Parse(json).RootElement;

    private static CanvasWidgetDto Widget(string id, string visibility, string config) => new()
    {
        Id = id, Type = "quote", X = 0, Y = 0, W = 1, H = 1,
        Visibility = visibility, Card = false, Config = Config(config),
    };

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ExportsTheSubjectsOwnProfile()
    {
        var profile = await SeedProfileAsync("user_subject", "subject");
        profile.Bio = "my own bio";
        profile.AccentColor = "#5865F2";
        profile.Font = ProfileFont.Serif;
        await _context.SaveChangesAsync();

        var response = await ExportAsync("user_subject");

        Assert.That(response.Service, Is.EqualTo("social"));
        Assert.That(response.RowCounts["profile"], Is.EqualTo(1));

        using var document = JsonDocument.Parse(response.FragmentJson);
        var exported = document.RootElement.GetProperty("profile");

        Assert.Multiple(() =>
        {
            Assert.That(exported.GetProperty("userName").GetString(), Is.EqualTo("subject"));
            Assert.That(exported.GetProperty("bio").GetString(), Is.EqualTo("my own bio"));
            Assert.That(exported.GetProperty("accentColor").GetString(), Is.EqualTo("#5865F2"));
        });
    }

    [Test]
    public async Task Handle_ExportsTheSubjectsSideOfEachRelationship()
    {
        var subject = await SeedProfileAsync("user_subject", "subject");
        var friend = await SeedProfileAsync("user_friend", "friend");

        var pair = Relationship.Create(new CreateRelationshipParams
        {
            Initiator = subject.Id,
            Subject = friend.Id,
        });

        _context.Relationships.AddRange(pair);
        await _context.SaveChangesAsync();

        var response = await ExportAsync("user_subject");

        Assert.That(response.RowCounts["relationships"], Is.EqualTo(1));

        using var document = JsonDocument.Parse(response.FragmentJson);
        var relationship = document.RootElement.GetProperty("relationships").EnumerateArray().Single();

        Assert.That(relationship.GetProperty("counterpartyProfileId").GetString(), Is.EqualTo(friend.Id));
    }

    // ── canvas ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_ExportsTheCanvasThemeAndWidgetsUnstripped()
    {
        var profile = await SeedProfileAsync("user_subject", "subject");

        var theme = new CanvasThemeDto { Accent = "#5865F2", Backdrop = new CanvasBackdropDto { Kind = "gradient", From = "#111111", To = "#222222" } };
        var widgets = new[]
        {
            Widget("widget-public", "everyone", """{"text":"hello"}"""),
            // A friends-only widget: the export is the owner's own data, never visibility-stripped.
            Widget("widget-friends", "friends", """{"nested":{"list":[1,2,3]}}"""),
        };
        var themeJson = JsonSerializer.Serialize(theme, CanvasJson.Options);
        var widgetsJson = JsonSerializer.Serialize(widgets, CanvasJson.Options);
        _context.ProfileCanvases.Add(ProfileCanvas.Create(profile.Id, themeJson, widgetsJson));
        await _context.SaveChangesAsync();

        var response = await ExportAsync("user_subject");

        Assert.That(response.RowCounts["canvas"], Is.EqualTo(1));

        using var document = JsonDocument.Parse(response.FragmentJson);
        var canvas = document.RootElement.GetProperty("canvas");

        Assert.That(canvas.GetProperty("theme").GetProperty("accent").GetString(), Is.EqualTo("#5865F2"));

        var exportedWidgets = canvas.GetProperty("widgets").EnumerateArray().ToList();
        Assert.That(exportedWidgets, Has.Count.EqualTo(2));

        var friendsWidget = exportedWidgets.Single(w => w.GetProperty("id").GetString() == "widget-friends");
        Assert.Multiple(() =>
        {
            Assert.That(friendsWidget.GetProperty("visibility").GetString(), Is.EqualTo("friends"));
            Assert.That(friendsWidget.GetProperty("config").GetProperty("nested").GetProperty("list").EnumerateArray().Select(e => e.GetInt32()), Is.EqualTo(new[] { 1, 2, 3 }));
        });
    }

    [Test]
    public async Task Handle_ExportsAReferenceToEachCanvasImage()
    {
        var profile = await SeedProfileAsync("user_subject", "subject");
        _context.ProfileCanvasImages.Add(ProfileCanvasImage.Create(profile.Id, "image/png", 1024));
        await _context.SaveChangesAsync();
        var image = _context.ProfileCanvasImages.Single(i => i.ProfileId == profile.Id);

        var response = await ExportAsync("user_subject");

        Assert.That(response.RowCounts["canvasImages"], Is.EqualTo(1));

        using var document = JsonDocument.Parse(response.FragmentJson);
        var exported = document.RootElement.GetProperty("canvasImages").EnumerateArray().Single();

        Assert.Multiple(() =>
        {
            Assert.That(exported.GetProperty("id").GetString(), Is.EqualTo(image.Id));
            Assert.That(exported.GetProperty("contentType").GetString(), Is.EqualTo("image/png"));
            Assert.That(exported.GetProperty("url").GetString(), Does.Contain($"/canvas-images/{image.Id}"));
        });
    }

    [Test]
    public async Task Handle_NoCanvas_OmitsItRatherThanFailing()
    {
        await SeedProfileAsync("user_subject", "subject");

        var response = await ExportAsync("user_subject");

        Assert.Multiple(() =>
        {
            Assert.That(response.RowCounts["canvas"], Is.EqualTo(0));
            Assert.That(response.RowCounts["canvasImages"], Is.EqualTo(0));
        });

        using var document = JsonDocument.Parse(response.FragmentJson);
        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("canvas").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(document.RootElement.GetProperty("canvasImages").GetArrayLength(), Is.EqualTo(0));
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_AccountWithNoProfile_ReturnsAnEmptyFragmentRatherThanThrowing()
    {
        var response = await ExportAsync("user_noprofile");

        Assert.Multiple(() =>
        {
            Assert.That(response.Service, Is.EqualTo("social"));
            Assert.That(response.RowCounts["profile"], Is.EqualTo(0));
            Assert.That(response.RowCounts["relationships"], Is.EqualTo(0));
            Assert.That(response.RowCounts["canvas"], Is.EqualTo(0));
            Assert.That(response.RowCounts["canvasImages"], Is.EqualTo(0));
        });
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Handle_DoesNotIncludeACounterpartysProfileData()
    {
        var subject = await SeedProfileAsync("user_subject", "subject");
        var friend = await SeedProfileAsync("user_friend", "SuperSecretUsername");
        friend.Bio = "the friend's own bio";
        friend.AccentColor = "#ABCDEF";
        await _context.SaveChangesAsync();

        var pair = Relationship.Create(new CreateRelationshipParams
        {
            Initiator = subject.Id,
            Subject = friend.Id,
        });

        _context.Relationships.AddRange(pair);
        await _context.SaveChangesAsync();

        var response = await ExportAsync("user_subject");

        Assert.Multiple(() =>
        {
            Assert.That(response.FragmentJson, Does.Not.Contain("SuperSecretUsername"));
            Assert.That(response.FragmentJson, Does.Not.Contain("the friend's own bio"));
            Assert.That(response.FragmentJson, Does.Not.Contain("#ABCDEF"));
            Assert.That(response.FragmentJson, Does.Not.Contain("user_friend"));
        });
    }

    [Test]
    public async Task Handle_DoesNotDiscloseBlocksPlacedAgainstTheSubject()
    {
        var subject = await SeedProfileAsync("user_subject", "subject");
        var blocker = await SeedProfileAsync("user_blocker", "blocker");

        // T0-3: a block must not be visible to the person blocked.
        var block = new Relationship
        {
            Id = "rlsp_block",
            OwnerId = blocker.Id,
            TargetId = subject.Id,
            Status = RelationshipStatus.Blocked,
        };

        _context.Relationships.Add(block);
        await _context.SaveChangesAsync();

        var response = await ExportAsync("user_subject");

        Assert.Multiple(() =>
        {
            Assert.That(response.RowCounts["relationships"], Is.EqualTo(0));
            Assert.That(response.FragmentJson, Does.Not.Contain("Blocked"));
        });
    }
}
