using System.Text.Json.Nodes;
using AppEnvironment;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Tracks;
using Echo.Voice.Transport;
using Guild.Application.Controllers;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Controllers;

/// <summary>
/// The video ceiling and the publisher cap at the guild channel publish endpoint.
/// </summary>
[TestFixture]
[NonParallelizable]
public class GuildVoicePublishEntitlementTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";
    private const string UserId = "user-1";
    private const string OwnerId = "owner-1";
    private const string PeerId = "peer-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissions = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _sfu = new FakeVoiceSfu();
        _permissions = new GuildPermissionService(
            _cache, _context, NullLogger<GuildPermissionService>.Instance);

        await SeedGuildAsync(Permissions.Connect | Permissions.Speak | Permissions.Stream);
        await SeedRoomAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedGuildAsync(Permissions rolePermissions)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "voice", Description = "d",
            Type = ChannelType.Voice,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = "role-1", GuildId = GuildId, Name = "member", Permissions = rolePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-1", GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = "role-1", MemberId = "member-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>The caller is in the room and one peer is already distributing a share, which is what
    /// makes the publisher cap answerable at all.</summary>
    private Task SeedRoomAsync() => VoiceTestHarness.SeedRoomAsync(_cache, new VoiceRoom
    {
        RoomId = ChannelId, Kind = VoiceRoomKind.Channel, GuildId = GuildId,
        Participants =
        [
            new VoiceParticipant { UserId = PeerId, MediaSessionId = "cf-peer", AudioTrackName = "audio",
                ActiveScreenShares = [new ActiveScreenShare
                {
                    ShareId = "share-a", MediaSessionId = "cf-peer",
                    TrackNames = [TrackNaming.ScreenTrack("share-a")],
                }] },
            new VoiceParticipant { UserId = UserId },
        ],
    });

    private FakeVoiceSfu _sfu = null!;

    private GuildVoiceMediaController ControllerFor(EntitlementResolver? entitlements) =>
        new(
            _sfu, _permissions,
            NullLogger<GuildVoiceMediaController>.Instance, _cache,
            VoiceTestHarness.ServiceFor(
                _cache, new FakeDistributedLockService(), new FakeHubContext(), entitlements))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.Create(UserId) },
            },
        };

    private static GuildPublishBody Camera(VoiceVideoIntent? video = null) =>
        new(["camera"], video);

    private static EntitlementResolver GuildPlan(Action<EntitlementSetBuilder> build) =>
        new([new ScriptedGuildPlan(build)]);

    private Task<IActionResult> PublishAsync(EntitlementResolver? plan, VoiceVideoIntent? video = null) =>
        ControllerFor(plan).Publish(GuildId, ChannelId, Camera(video), CancellationToken.None);

    // ══════════════════════════════════════════════════════════════════════════
    // Reduction: a 200 that says what it cost
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task A_request_above_the_guilds_rung_is_a_200_carrying_the_degradation()
    {
        var result = await PublishAsync(
            GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "720p30")),
            new VoiceVideoIntent(1080, 60));

        var body = (result as OkObjectResult)?.Value as JsonNode;
        var degradations = body?["degradations"]?.AsArray();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>(),
                "a degradation is a 200; an error status makes every existing client path roll back, "
                + "which is a denial with extra steps");
            Assert.That(body?["maxLayer"], Is.Not.Null,
                "and the reply still carries the layer cap the roster now holds");
            Assert.That(degradations, Has.Count.EqualTo(1));
            Assert.That((string?)degradations![0]!["key"],
                Is.EqualTo(EntitlementKeys.VoiceVideoCeiling.Name));
            Assert.That((string?)degradations[0]!["reason"],
                Is.EqualTo(EntitlementReasonCodes.PairedCeiling),
                "both sides of the paired key carry a ceiling - the member's falls to the ladder's "
                + "top when they have no plan of their own - so this is the pair, and the guild won");
            Assert.That((string?)degradations[0]!["boundBy"], Is.EqualTo(EntitlementBoundBy.Guild),
                "without the side, this is where a paying member is told their own plan limited them");
            Assert.That((string?)degradations[0]!["requested"]!["rung"], Is.EqualTo("1080p60"));
            Assert.That((string?)degradations[0]!["granted"]!["rung"], Is.EqualTo("720p30"),
                "the granted rung rides the reply, so the client can re-encode without a second "
                + "round trip");
        });
    }

    /// <summary>The clamp is advice.</summary>
    [Test]
    public async Task A_clamped_publish_records_the_layer_ceiling_on_the_roster()
    {
        await PublishAsync(
            GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "720p30")),
            new VoiceVideoIntent(1080, 60));

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));

        Assert.That(room!.Find(UserId)!.MaxVideoLayer, Is.EqualTo(VoiceVideoLayer.Medium),
            "otherwise a publisher who ignores the clamp still has their top layer fanned out and "
            + "the ceiling has cost them nothing");
    }

    /// <summary>
    /// A v1 client sends no <c>video</c> at all, and that has to resolve to the ceiling rather than
    /// to nothing - so it publishes, and it is told the room reduced it.
    /// </summary>
    [Test]
    public async Task A_client_that_declares_no_video_still_publishes_and_is_still_told()
    {
        var result = await PublishAsync(
            GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "720p30")));

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));
        var degradations = ((result as OkObjectResult)?.Value as JsonNode)?["degradations"]?.AsArray();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That((string?)degradations![0]!["granted"]!["rung"], Is.EqualTo("720p30"));
            Assert.That(room!.Find(UserId)!.MaxVideoLayer, Is.Null,
                "an unstated request is a request for the ceiling, not a claim this server can bind "
                + "a layer to - capping it would halve the quality of every released client");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Refusal: the two things with nothing below them
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task A_guild_with_no_rung_left_refuses_the_publish_with_the_denial_body()
    {
        var result = await PublishAsync(
            GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "none")),
            new VoiceVideoIntent(720, 30));

        var denial = (result as ObjectResult)?.Value as EntitlementDenialDto;

        Assert.Multiple(() =>
        {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(403),
                "never 429, which the clients' rate-limit interceptor retries three times and "
                + "swallows the body of, and never 401, which signs the user out");
            Assert.That(denial, Is.Not.Null);
            Assert.That(denial!.Key, Is.EqualTo(EntitlementKeys.VoiceVideoCeiling.Name));
            Assert.That(denial.Code, Is.EqualTo(denial.Reason));
            Assert.That(denial.Reason, Is.EqualTo(EntitlementReasonCodes.PairedCeiling));
            Assert.That(denial.BoundBy, Is.EqualTo(EntitlementBoundBy.Guild),
                "the paired key names the side that bound, refusal and reduction alike");
            Assert.That(denial.Retryable, Is.False,
                "retrying an entitlement refusal turns one refusal into three");
        });
    }

    [Test]
    public async Task A_publisher_cap_with_no_slot_free_refuses_and_says_how_full_the_room_is()
    {
        var result = await PublishAsync(
            GuildPlan(b => b.Number(EntitlementKeys.VoiceMaxPublishers, 1)));

        var denial = (result as ObjectResult)?.Value as EntitlementDenialDto;

        Assert.Multiple(() =>
        {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(403));
            Assert.That(denial!.Key, Is.EqualTo(EntitlementKeys.VoiceMaxPublishers.Name));
            Assert.That(denial.Requested!.Value, Is.EqualTo(2));
            Assert.That(denial.Granted!.Value, Is.EqualTo(1),
                "'2 of 2 people are sharing' is a sentence; 'you cannot share' is a mystery");
        });
    }

    /// <summary>
    /// A body carrying a microphone and a camera together is refused whole, and the microphone is
    /// not recorded as published.
    /// </summary>
    [Test]
    public async Task A_mixed_body_that_would_be_refused_is_refused_whole()
    {
        var mixed = new GuildPublishBody([TrackNaming.Audio, "camera"]);

        var result = await ControllerFor(GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "none")))
            .Publish(GuildId, ChannelId, mixed, CancellationToken.None);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));

        Assert.Multiple(() =>
        {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(403));
            Assert.That(room!.Find(UserId)!.PublishState, Is.EqualTo(VoicePublishState.Joined),
                "nothing in the offer was accepted, so nothing about it is recorded");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The remedy, which only the server can compute
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whether this caller could actually buy the upgrade is resolved per request, against
    /// <c>ManageGuild</c>, and it is the one field a client must never compute for itself: a
    /// re-implementation of permission evaluation in a client is how you get a buy button that 403s.
    /// </summary>
    [TestCase(true, TestName = "A_member_who_can_manage_the_guild_is_offered_the_upgrade")]
    [TestCase(false, TestName = "A_member_who_cannot_is_told_an_admin_can")]
    public async Task The_remedy_is_resolved_against_ManageGuild(bool canManage)
    {
        using var hosted = HostedInstance();

        await ReplaceRolePermissionsAsync(canManage
            ? Permissions.Connect | Permissions.Speak | Permissions.Stream | Permissions.ManageGuild
            : Permissions.Connect | Permissions.Speak | Permissions.Stream);

        var result = await PublishAsync(
            GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "none")),
            new VoiceVideoIntent(720, 30));

        var denial = (result as ObjectResult)?.Value as EntitlementDenialDto;

        Assert.Multiple(() =>
        {
            Assert.That(denial!.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeGuild));
            Assert.That(denial.ActorCanRemedy, Is.EqualTo(canManage));
        });
    }

    /// <summary>An instance that sells nothing - every self-hosted one - offers no remedy at all. An
    /// upgrade link to a service that is not deployed is worse than no link.</summary>
    [Test]
    public async Task An_instance_that_sells_nothing_offers_no_remedy()
    {
        var result = await PublishAsync(
            GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "none")),
            new VoiceVideoIntent(720, 30));

        var denial = (result as ObjectResult)?.Value as EntitlementDenialDto;

        Assert.Multiple(() =>
        {
            Assert.That(denial!.Remedy, Is.EqualTo(EntitlementRemedyCodes.None));
            Assert.That(denial.ActorCanRemedy, Is.False, "there is no remedy, so nobody can perform it");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The mid-stream re-declaration, which is how a resolution changes without a republish
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The endpoint half of the hole.</summary>
    [Test]
    public async Task A_renegotiation_above_the_rung_caps_a_publisher_who_published_inside_it()
    {
        var plan = GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "720p30"));

        await PublishAsync(plan, new VoiceVideoIntent(720, 30));
        var published = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));

        var result = await ControllerFor(plan).DeclareVideo(
            GuildId, ChannelId, new VoiceVideoIntent(1080, 60), CancellationToken.None);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));

        Assert.Multiple(() =>
        {
            Assert.That(published!.Find(UserId)!.MaxVideoLayer, Is.Null,
                "the publish itself declared a size inside the rung");
            Assert.That(room!.Find(UserId)!.MaxVideoLayer, Is.EqualTo(VoiceVideoLayer.Medium));
            Assert.That(result, Is.InstanceOf<OkObjectResult>(),
                "a re-declaration is information, not a request, and is never refused by any of "
                + "this");
        });
    }

    /// <summary>A re-declaration that declares nothing is every client that has nothing to say. It
    /// must leave the recorded cap exactly where it is: reading absence as "uncapped" would make the
    /// cheapest way past the ceiling be to publish honestly once and then renegotiate empty.</summary>
    [Test]
    public async Task A_renegotiation_that_declares_nothing_leaves_the_cap_alone()
    {
        var plan = GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "720p30"));

        await PublishAsync(plan, new VoiceVideoIntent(1080, 60));

        await ControllerFor(plan).DeclareVideo(
            GuildId, ChannelId, new VoiceVideoIntent(), CancellationToken.None);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));

        Assert.That(room!.Find(UserId)!.MaxVideoLayer, Is.EqualTo(VoiceVideoLayer.Medium),
            "absent is not a request to be uncapped");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // What is deliberately untouched
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The video ceiling has never had anything to say about a microphone, and the most
    /// restrictive ceiling expressible must not be able to silence a channel.</summary>
    [Test]
    public async Task An_audio_only_publish_is_never_measured_against_the_video_ceiling()
    {
        var audio = new GuildPublishBody([TrackNaming.Audio]);

        var result = await ControllerFor(GuildPlan(b => b.Rung(EntitlementKeys.VoiceVideoCeiling, "none")))
            .Publish(GuildId, ChannelId, audio, CancellationToken.None);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(room!.Find(UserId)!.PublishState, Is.EqualTo(VoicePublishState.Publishing));
        });
    }

    /// <summary>Every deployment today: no resolver wired, nothing capped, and a reply identical to
    /// the one this endpoint has always given. Absent and empty mean the same thing to a client, and
    /// absent is what a v1 response looked like.</summary>
    [Test]
    public async Task A_publish_with_nothing_to_bind_it_answers_exactly_as_it_always_did()
    {
        var result = await PublishAsync(null);

        Assert.That(result, Is.InstanceOf<OkObjectResult>(),
            "and the reply carries no degradations block at all, which is what a v1 response looked "
            + "like");
    }

    // ══════════════════════════════════════════════════════════════════════════ Fixtures
    // ══════════════════════════════════════════════════════════════════════════

    private async Task ReplaceRolePermissionsAsync(Permissions permissions)
    {
        _context.Roles.Single(r => r.Id == "role-1").Permissions = permissions;
        await _context.SaveChangesAsync();
        await _cache.RemoveAsync(GuildPermissionsForUser.GetCacheKey(GuildId, UserId));
        await _cache.RemoveAsync(GuildChannelPermission.GetCacheKey(GuildId, ChannelId, UserId));
    }

    /// <summary>An instance that has billing configured, which is the only state in which any remedy
    /// is offered at all. The license configuration is process-wide and is read once from the
    /// environment at startup, so it is set and put back around the test - which is what
    /// <c>NonParallelizable</c> on this fixture is for.</summary>
    private static IDisposable HostedInstance() => new HostedScope();

    private sealed class HostedScope : IDisposable
    {
        private readonly string _mode = Env.License.Mode;
        private readonly string _billing = Env.License.BillingServiceUrl;

        public HostedScope()
        {
            Env.License.Mode = LicenseConfiguration.Hosted;
            Env.License.BillingServiceUrl = "http://billing.test";
        }

        public void Dispose()
        {
            Env.License.Mode = _mode;
            Env.License.BillingServiceUrl = _billing;
        }
    }

    /// <summary>A guild-scoped plan and nothing else, which is how a real plan source behaves: the
    /// resolver drops any key whose scope does not match the subject it was asked about.</summary>
    private sealed class ScriptedGuildPlan(Action<EntitlementSetBuilder> build) : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => EntitlementPrecedence.Subscription;

        public Task<EntitlementSet> ResolveAsync(
            EntitlementSubject subject, CancellationToken cancellationToken)
        {
            if (subject.Kind != SubjectKind.Guild) return Task.FromResult(EntitlementSet.Empty);

            var builder = new EntitlementSetBuilder(EntitlementPrecedence.Subscription);
            build(builder);
            return Task.FromResult(builder.Build());
        }
    }
}
