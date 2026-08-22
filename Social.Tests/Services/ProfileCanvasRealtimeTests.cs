using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

/// <summary>The social.ProfileCanvasUpdated fan-out, and what each recipient is allowed to receive.</summary>
[TestFixture]
public class ProfileCanvasRealtimeTests
{
    private TestSocialContext _context = null!;
    private FakeSocialHubContext _hub = null!;
    private Profile _owner = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _hub = new FakeSocialHubContext();
        _owner = await AddProfile("user-owner", "owner");
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

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

    private static CanvasWidgetDto Widget(string id, string visibility, double y) => new()
    {
        Id = id, Type = "quote", X = 0, Y = y, W = 1, H = 1,
        Visibility = visibility, Card = false, Config = JsonDocument.Parse("{}").RootElement,
    };

    private async Task<ProfileCanvasDto> PublishAsync(ISharedGuildResolver sharedGuilds, params CanvasWidgetDto[] widgets)
    {
        var canvases = new ProfileCanvasService(_context, sharedGuilds);
        var realtime = new ProfileCanvasRealtime(canvases, _hub, NullLogger<ProfileCanvasRealtime>.Instance);

        var canvas = await canvases.SaveAsync(
            _owner.Id, new CanvasWriteDto { Theme = new CanvasThemeDto(), Widgets = widgets });

        await realtime.PublishAsync(_owner, canvas);
        return canvas;
    }

    private IReadOnlyList<string> WidgetIdsSentTo(string userId) =>
        _hub.To(userId).Single().Payload<ProfileCanvasUpdatedPayload>().Canvas.Widgets.Select(w => w.Id).ToList();

    [Test]
    public async Task The_owner_receives_the_event_carrying_their_whole_canvas()
    {
        await PublishAsync(new NoSharedGuildResolver(),
            Widget("open", "everyone", 0), Widget("mates", "friends", 1), Widget("shared", "mutuals", 2));

        Assert.That(_hub.Sent.Select(s => s.Method), Is.All.EqualTo(ProfileCanvasRealtime.EventName));
        Assert.That(WidgetIdsSentTo("user-owner"), Is.EquivalentTo(new[] { "open", "mates", "shared" }));
    }

    [Test]
    public async Task The_payload_carries_the_profile_id_alongside_the_canvas()
    {
        await PublishAsync(new NoSharedGuildResolver(), Widget("open", "everyone", 0));

        var payload = _hub.To("user-owner").Single().Payload<ProfileCanvasUpdatedPayload>();

        Assert.Multiple(() =>
        {
            Assert.That(payload.ProfileId, Is.EqualTo(_owner.Id));
            Assert.That(payload.Canvas.ProfileId, Is.EqualTo(_owner.Id));
            Assert.That(payload.Canvas.Version, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_friend_receives_a_copy_with_the_mutuals_widget_stripped()
    {
        var friend = await AddProfile("user-friend", "friend");
        await Befriend(_owner, friend);

        await PublishAsync(new NoSharedGuildResolver(),
            Widget("open", "everyone", 0), Widget("mates", "friends", 1), Widget("shared", "mutuals", 2));

        Assert.That(WidgetIdsSentTo("user-friend"), Is.EquivalentTo(new[] { "open", "mates" }),
            "a friend who shares no guild and no third friend is not a mutual");
    }

    [Test]
    public async Task A_friend_who_shares_a_guild_receives_the_mutuals_widget()
    {
        var friend = await AddProfile("user-friend", "friend");
        await Befriend(_owner, friend);

        await PublishAsync(new StubSharedGuildResolver("user-friend"),
            Widget("open", "everyone", 0), Widget("shared", "mutuals", 1));

        Assert.That(WidgetIdsSentTo("user-friend"), Is.EquivalentTo(new[] { "open", "shared" }));
    }

    [Test]
    public async Task Nobody_outside_the_owner_and_their_friends_is_sent_anything()
    {
        await AddProfile("user-stranger", "stranger");

        await PublishAsync(new NoSharedGuildResolver(), Widget("mates", "friends", 0));

        Assert.Multiple(() =>
        {
            Assert.That(_hub.Sent, Has.Count.EqualTo(1));
            Assert.That(_hub.To("user-stranger"), Is.Empty);
        });
    }

    [Test]
    public async Task No_recipient_other_than_the_owner_is_ever_sent_an_unstripped_canvas()
    {
        var friend = await AddProfile("user-friend", "friend");
        await Befriend(_owner, friend);

        await PublishAsync(new NoSharedGuildResolver(),
            Widget("open", "everyone", 0), Widget("mates", "friends", 1), Widget("shared", "mutuals", 2));

        var leaked = _hub.Sent
            .Where(s => !s.Recipients.Contains("user-owner"))
            .Select(s => s.Payload<ProfileCanvasUpdatedPayload>())
            .Where(p => p.Canvas.Widgets.Any(w => w.Id == "shared"));

        Assert.That(leaked, Is.Empty);
    }
}
