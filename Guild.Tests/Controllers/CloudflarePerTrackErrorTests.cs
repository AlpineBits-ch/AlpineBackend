using System.Net;
using System.Text;
using Echo.Realtime.Caching;
using Echo.Realtime.Sfu;
using Guild.Application.Controllers;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Controllers;

/// <summary>
/// Pins the defect that makes every other voice failure permanent and invisible.
///
/// <para>Cloudflare Calls reports a <em>per-track</em> failure inside an HTTP <b>200</b>: the
/// response carries a normal <c>sessionDescription</c>, and the failing entry in <c>tracks[]</c>
/// gets <c>errorCode</c>/<c>errorDescription</c> instead of a <c>mid</c>. Pulling a track that does
/// not exist on the publisher's session - the single most common transient outcome, and the exact
/// thing <c>TracksNewWithRetryAsync</c> was written to absorb - arrives this way.</para>
///
/// <para><see cref="CloudflareService"/> only inspects the HTTP status, and
/// <see cref="CfTrackResult"/>'s <c>Error</c> property does not correspond to any field Cloudflare
/// actually sends. So the failure is erased at the proxy:</para>
/// <list type="bullet">
/// <item>no exception, therefore <c>TracksNewWithRetryAsync</c> never retries - the retry loop is
/// dead code for the case it exists for, because it only catches
/// <see cref="CloudflareCallsException"/> and that is only thrown on a non-2xx;</item>
/// <item>the controller answers 200 with a track that has no mid and no error, so the client cannot
/// distinguish it from a successful subscribe. Alpine then falls back to its own local transceiver
/// mid (<c>?? transceiver.mid</c>) and mobile to <c>?? ''</c>, both record it in their mid map, and
/// neither rolls back the per-user dedupe guard. The participant is marked subscribed, is never
/// retried, and is rendered as "No audio received from this participant" forever.</item>
/// </list>
///
/// <para><see cref="CloudflareService"/> lives in Echo.Realtime and is shared by both voice
/// deployments, so this covers the Messaging (1:1 call) path as well - its
/// <c>CloudflareController.TracksNewWithRetryAsync</c> is character-for-character the same loop.</para>
/// </summary>
[TestFixture]
public class CloudflarePerTrackErrorTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";
    private const string UserId = "user-1";

    /// <summary>A real Cloudflare "the track you asked to pull isn't there" response: 200, a valid
    /// session description, and the error reported on the track entry itself.</summary>
    private const string TrackNotFoundBody = """
        {
          "requiresImmediateRenegotiation": false,
          "sessionDescription": { "type": "answer", "sdp": "v=0" },
          "tracks": [
            {
              "trackName": "audio",
              "sessionId": "cf-remote-session",
              "errorCode": "TrackNotFound",
              "errorDescription": "Track not found"
            }
          ]
        }
        """;

    private CountingCloudflareHandler _handler = null!;
    private CloudflareService _cfService = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new CountingCloudflareHandler(TrackNotFoundBody);
        _cfService = new CloudflareService(
            new SingleHandlerFactory(_handler), NullLogger<CloudflareService>.Instance);
    }

    [TearDown]
    public void TearDown() => _handler.Dispose();

    private static CfTracksNewRequest SubscribeRequest() => new(
        new CfSessionDescription("offer", "v=0"),
        [new CfTrackNew("remote", SessionId: "cf-remote-session", TrackName: "audio")]);

    // ══════════════════════════════════════════════════════════════════════════
    // CloudflareService: the error never survives deserialisation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void TracksNew_WithAPerTrackError_SurfacesTheFailureToTheCaller()
    {
        // Nothing above this layer can react to a failure it is never told about, and returning it
        // as a success is what let the retry loops below sit dead.
        Assert.That(async () => await _cfService.TracksNewAsync("cf-local-session", SubscribeRequest()),
            Throws.TypeOf<CloudflareCallsException>());
    }

    [Test]
    public void TracksNew_WithAPerTrackError_CarriesCloudflaresOwnDiagnosticsOnTheException()
    {
        // The raw body is the only place Cloudflare's reason survives; without it the next
        // occurrence is diagnosable only from the client's side, which sees even less.
        Assert.That(async () => await _cfService.TracksNewAsync("cf-local-session", SubscribeRequest()),
            Throws.TypeOf<CloudflareCallsException>()
                .With.Property(nameof(CloudflareCallsException.ResponseBody)).Contains("Track not found"));
    }

    [Test]
    public void TracksNew_WithATrackThatCameBackWithoutAMid_IsAlsoTreatedAsAFailure()
    {
        // A track with no mid is unusable to the caller whether or not Cloudflare labelled it an
        // error - and it is precisely the shape the clients used to paper over by substituting a
        // locally invented mid.
        using var handler = new CountingCloudflareHandler("""
            {
              "requiresImmediateRenegotiation": false,
              "sessionDescription": { "type": "answer", "sdp": "v=0" },
              "tracks": [ { "trackName": "audio", "sessionId": "cf-remote-session" } ]
            }
            """);
        var service = new CloudflareService(
            new SingleHandlerFactory(handler), NullLogger<CloudflareService>.Instance);

        Assert.That(async () => await service.TracksNewAsync("cf-local-session", SubscribeRequest()),
            Throws.TypeOf<CloudflareCallsException>());
    }

    [Test]
    public async Task TracksNew_WithHealthyTracks_ReturnsThemUntouched()
    {
        // The working path has to stay working: a good response is relayed verbatim, no throw.
        using var handler = new CountingCloudflareHandler("""
            {
              "requiresImmediateRenegotiation": true,
              "sessionDescription": { "type": "answer", "sdp": "v=0" },
              "tracks": [ { "trackName": "audio", "mid": "1", "sessionId": "cf-remote-session" } ]
            }
            """);
        var service = new CloudflareService(
            new SingleHandlerFactory(handler), NullLogger<CloudflareService>.Instance);

        var result = await service.TracksNewAsync("cf-local-session", SubscribeRequest());

        Assert.Multiple(() =>
        {
            Assert.That(result.Tracks[0].Mid, Is.EqualTo("1"));
            Assert.That(handler.TracksNewCount, Is.EqualTo(1), "a healthy response must not be retried");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Controller: the retry loop that exists for this case never runs
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SubscribeWithAPerTrackError_RetriesLikeAnyOtherTransientCloudflareFailure()
    {
        var controller = BuildController();

        await controller.TracksNew(GuildId, ChannelId, SubscribeBody(), CancellationToken.None);

        // TracksNewWithRetryAsync's own comment: "Subscribing to a track another participant only
        // just published can race Cloudflare's own SFU eventual consistency... A few short retries
        // absorb that window instead of leaving the participant permanently, silently unheard."
        // That window presents as this 200, so the loop it was written for never fires.
        Assert.That(_handler.TracksNewCount, Is.GreaterThan(1),
            "a per-track 'Track not found' is the propagation race the retry loop was added for, "
            + "but arrives as a 200 and so is never retried");
    }

    [Test]
    public async Task SubscribeWithAPerTrackError_DoesNotReturnPlain200ToTheClient()
    {
        var controller = BuildController();

        var result = await controller.TracksNew(GuildId, ChannelId, SubscribeBody(), CancellationToken.None);

        // A 200 with an empty-but-well-formed body is indistinguishable from a working subscribe.
        // The client marks the user subscribed, and every retry path it has is gated behind that
        // same guard.
        Assert.That(result, Is.Not.InstanceOf<OkObjectResult>(),
            "the client has no way to tell this apart from a successful subscribe");
    }

    [Test]
    public async Task SubscribeThatSucceeds_ReturnsOkWithoutRetrying()
    {
        // Control: the retry loop must not fire on the happy path.
        using var handler = new CountingCloudflareHandler("""
            {
              "requiresImmediateRenegotiation": false,
              "sessionDescription": { "type": "answer", "sdp": "v=0" },
              "tracks": [ { "trackName": "audio", "mid": "1", "sessionId": "cf-remote-session" } ]
            }
            """);
        var controller = BuildController(new CloudflareService(
            new SingleHandlerFactory(handler), NullLogger<CloudflareService>.Instance));

        var result = await controller.TracksNew(GuildId, ChannelId, SubscribeBody(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(handler.TracksNewCount, Is.EqualTo(1));
        });
    }

    // ── Fixture plumbing ──────────────────────────────────────────────────────

    /// <summary>Subscribe-only body: all tracks remote, so it takes the retry path.</summary>
    private static GuildTracksNewBody SubscribeBody() => new(
        "cf-local-session",
        new CfSessionDescription("offer", "v=0"),
        [new CfTrackNew("remote", SessionId: "cf-remote-session", TrackName: "audio")]);

    /// <summary>
    /// A subscribe is media access, so TracksNew now requires Connect on the channel and requires
    /// the acting session to be one the caller minted. (It previously required neither, which is
    /// how an all-"remote" body reached Cloudflare completely unauthorized.) These tests are about
    /// the retry loop, so the fixture satisfies both and moves on.
    /// </summary>
    private GuildCloudflareController BuildController(CloudflareService? cfService = null)
    {
        var context = new TestGuildContext(Guid.NewGuid().ToString());
        var cache = new FakeDistributedCache();

        context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner-1", Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "voice", Description = "d", Type = ChannelType.Voice,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        context.Roles.Add(new Role
        {
            Id = "role-connect", GuildId = GuildId, Name = "connect", Permissions = Permissions.Connect,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        context.GuildMembers.Add(new GuildMember
        {
            Id = "member-1", GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}",
        });
        context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = "role-connect", MemberId = "member-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        context.SaveChanges();

        // The session the body acts as has to belong to this caller.
        cache.SetEntry("guild-cf-session-owner:cf-local-session", UserId);

        return new GuildCloudflareController(
            cfService ?? _cfService,
            new GuildPermissionService(cache, context, NullLogger<GuildPermissionService>.Instance),
            new FakeHubContext(), NullLogger<GuildCloudflareController>.Instance, cache,
            new LockedJsonCacheStore(new FakeDistributedLockService(), cache), context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.Create(UserId) },
            },
        };
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://cloudflare.test/") };
    }

    /// <summary>Answers every tracks/new with <paramref name="tracksNewBody"/> and counts the
    /// attempts, so a missing retry is observable.</summary>
    private sealed class CountingCloudflareHandler(string tracksNewBody) : HttpMessageHandler
    {
        public int TracksNewCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.EndsWith("sessions/new"))
            {
                body = """{"sessionId":"cf-local-session"}""";
            }
            else if (path.EndsWith("tracks/new"))
            {
                TracksNewCount++;
                body = tracksNewBody;
            }
            else
            {
                body = """{"sessionDescription":{"type":"answer","sdp":"v=0"},"tracks":[]}""";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
