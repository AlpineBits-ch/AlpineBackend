using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Sources;
using Echo.Entitlements.Wire;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
using Echo.Voice.Sessions;
using Echo.Voice.Testing;
using Echo.Voice.Tracks;
using Echo.Voice.Transport;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Controllers;

/// <summary>The video ceiling at the direct-call publish endpoint.</summary>
[TestFixture]
public class CallVoicePublishEntitlementTests
{
    private const string CallId = "call-1";
    private const string UserId = "user-1";
    private const string SessionId = "cf-local-session";

    private FakeDistributedCache _cache = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public async Task SetUp()
    {
        _cache = new FakeDistributedCache();
        _bus = new FakeMessageBus(msg => msg switch
        {
            ValidateUserDeviceRequest => new ValidateUserDeviceResponse { IsRegistered = true },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

        await SeedConnectedCallerAsync();
    }

    /// <summary>Participation is what entitles the caller to this call's media at all, and session
    /// ownership is what stops them acting as somebody else's. Both are checked before any of this
    /// runs, so they are set up once and got out of the way.</summary>
    private async Task SeedConnectedCallerAsync()
    {
        var call = new Call
        {
            Id = CallId, ConversationId = "conv-1", CreatorId = UserId,
            Participants = [new CallParticipant { UserId = UserId, Status = CallStatus.Connected }],
        };

        await _cache.SetAsync(
            Call.GetCacheId(CallId), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());
        await _cache.SetAsync($"voice:session-owner:{SessionId}", Encoding.UTF8.GetBytes(UserId), new());

        await VoiceTestHarness.SeedRoomAsync(_cache, new VoiceRoom
        {
            RoomId = CallId, Kind = VoiceRoomKind.Call,
            Participants = [new VoiceParticipant { UserId = UserId }],
        });
    }

    private CallVoiceMediaController ControllerFor(
        EntitlementResolver? entitlements = null, OperatorCeilings? ceilings = null)
    {
        var locks = new FakeDistributedLockService();

        return new CallVoiceMediaController(
            new CloudflareMediaTransport(StubCloudflareHttp.CreateService()), _cache,
            new LockedJsonCacheStore(locks, _cache), _bus,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            new SfuSessionOwnership(_cache),
            VoiceTestHarness.ServiceFor(
                _cache, locks, new FakeMessagingHubContext(), entitlements, ceilings),
            VoiceTestHarness.StoreFor(_cache, locks),
            NullLogger<CallVoiceMediaController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.ForUser(UserId) },
            },
        };
    }

    private static NegotiateBody Camera(VoiceVideoIntent? video = null) => new(
        SessionId,
        new VoiceSessionDescription("offer", "v=0"),
        [new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: "camera")],
        video);

    private static NegotiateBody Microphone() => new(
        SessionId,
        new VoiceSessionDescription("offer", "v=0"),
        [new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: TrackNaming.Audio)]);

    /// <summary>A box whose operator has decided it carries no video at all.</summary>
    private static OperatorCeilings NoVideo() => OperatorCeilings.Parse(
        new Dictionary<string, string?> { [EntitlementKeys.VoiceVideoCeiling.Name] = "none" });

    // ══════════════════════════════════════════════════════════════════════════ Refusal
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task A_video_publish_with_no_rung_left_is_refused_with_the_denial_body()
    {
        var result = await ControllerFor(ceilings: NoVideo())
            .Negotiate(CallId, Camera(), CancellationToken.None);

        var denial = (result as ObjectResult)?.Value as EntitlementDenialDto;

        Assert.Multiple(() =>
        {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(EntitlementDenialDto.StatusCode));
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(403),
                "never 429, which the clients' rate-limit interceptor retries three times and "
                + "swallows the body of, and never 401, which signs the user out");
            Assert.That(denial, Is.Not.Null);
            Assert.That(denial!.Key, Is.EqualTo(EntitlementKeys.VoiceVideoCeiling.Name));
            Assert.That(denial.Code, Is.EqualTo(denial.Reason),
                "one lookup table in the client serves refusals and degradations both");
            Assert.That(denial.Reason, Is.EqualTo(EntitlementReasonCodes.OperatorCeiling));
            Assert.That(denial.Remedy, Is.EqualTo(EntitlementRemedyCodes.None),
                "no amount of money moves an operator ceiling");
            Assert.That(denial.ActorCanRemedy, Is.False);
            Assert.That(denial.Retryable, Is.False);
        });
    }

    /// <summary>
    /// A body carrying both a microphone and a camera is refused whole, and the microphone is not
    /// recorded as published.
    /// </summary>
    [Test]
    public async Task A_mixed_body_that_would_be_refused_is_refused_whole()
    {
        var mixed = new NegotiateBody(
            SessionId,
            new VoiceSessionDescription("offer", "v=0"),
            [
                new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "0", TrackName: TrackNaming.Audio),
                new VoiceTrackRef(VoiceTrackDirection.Publish, Mid: "1", TrackName: "camera"),
            ]);

        var result = await ControllerFor(ceilings: NoVideo())
            .Negotiate(CallId, mixed, CancellationToken.None);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Call(CallId));

        Assert.Multiple(() =>
        {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(403));
            Assert.That(room!.Find(UserId)!.PublishState, Is.EqualTo(VoicePublishState.Joined),
                "nothing in the offer was accepted, so nothing about it is recorded");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Reduction
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The caller's own plan is the only commercial ceiling a call has, and asking for more
    /// than it covers is a 200 that says so - not a refusal, and not a silent downgrade.</summary>
    [Test]
    public async Task A_request_above_the_callers_own_ceiling_is_a_200_carrying_the_degradation()
    {
        var resolver = new EntitlementResolver([
            new ScriptedUserPlan(EntitlementKeys.VoiceVideoCeiling, "480p30"),
        ]);

        var result = await ControllerFor(resolver)
            .Negotiate(CallId, Camera(new VoiceVideoIntent(1080, 60)), CancellationToken.None);

        var body = (result as OkObjectResult)?.Value as JsonNode;
        var degradations = body?["degradations"]?.AsArray();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>(),
                "a degradation is a 200; an error status makes every existing client path roll back");
            Assert.That(body?["sessionDescription"], Is.Not.Null,
                "and the body the client already parses is still all of it");
            Assert.That(degradations, Has.Count.EqualTo(1));
            Assert.That((string?)degradations![0]!["key"],
                Is.EqualTo(EntitlementKeys.VoiceVideoCeiling.Name));
            Assert.That((string?)degradations[0]!["reason"],
                Is.EqualTo(EntitlementReasonCodes.PairedCeiling));
            Assert.That((string?)degradations[0]!["boundBy"], Is.EqualTo(EntitlementBoundBy.User),
                "without the side, this is where a paying member is told their own plan limited them");
            Assert.That((string?)degradations[0]!["granted"]!["rung"], Is.EqualTo("480p30"),
                "the granted rung rides the reply, so re-encoding costs no second round trip");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // What is deliberately untouched
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The video ceiling has never had anything to say about a microphone, and the most
    /// restrictive ceiling expressible must not be able to end a call.</summary>
    [Test]
    public async Task An_audio_only_publish_is_never_measured_against_the_video_ceiling()
    {
        var result = await ControllerFor(ceilings: NoVideo())
            .Negotiate(CallId, Microphone(), CancellationToken.None);

        var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Call(CallId));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(room!.Find(UserId)!.PublishState, Is.EqualTo(VoicePublishState.Publishing));
        });
    }

    /// <summary>The shipped state of every deployment: nothing resolved, nothing capped, and a reply
    /// byte-identical to the one this endpoint has always given.</summary>
    [Test]
    public async Task A_publish_with_nothing_to_bind_it_answers_exactly_as_it_always_did()
    {
        var result = await ControllerFor().Negotiate(CallId, Camera(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That((result as OkObjectResult)?.Value, Is.InstanceOf<VoiceNegotiateResponse>(),
                "absent and empty mean the same thing to a client, and absent is the v1 reply");
        });
    }

    /// <summary>A user-scoped plan and nothing else, which is all a direct call can be charged
    /// against.</summary>
    private sealed class ScriptedUserPlan(EntitlementKey key, string rung) : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => EntitlementPrecedence.Subscription;

        public Task<EntitlementSet> ResolveAsync(
            EntitlementSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult(subject.Kind != SubjectKind.User
                ? EntitlementSet.Empty
                : new EntitlementSetBuilder(EntitlementPrecedence.Subscription)
                    .Rung(key, rung)
                    .Build());
    }
}
