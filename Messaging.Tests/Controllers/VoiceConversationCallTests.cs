using Echo.Voice.Testing;
using Echo.Voice.Rooms;
using System.Text;
using System.Text.Json;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Controllers;

/// <summary>
/// Covers the conversation-level view of a call: <c>GET voice/conversations/{id}/call</c>, which is
/// how a member discovers a call already in progress, and the screen-share viewer endpoints.
/// </summary>
[TestFixture]
public class VoiceConversationCallTests
{
    private const string CallId = "call-1";
    private const string ConversationId = "conv-1";
    private const string Caller = "user-caller";
    private const string Callee = "user-callee";
    private const string Bystander = "user-bystander";
    private const string Stranger = "user-stranger";

    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;
    private TestMessagingContext _context = null!;
    private StreamViewerStore _viewers = null!;
    private FakeMessageBus _bus = null!;

    private FakeHubClients HubClients => (FakeHubClients)_hub.Clients;

    [SetUp]
    public async Task SetUp()
    {
        _cache = new FakeDistributedCache();
        _hub = new FakeMessagingHubContext();
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _viewers = new StreamViewerStore(new FakeDistributedLockService(), _cache);
        _bus = new FakeMessageBus(msg => msg switch
        {
            ValidateUserDeviceRequest => new ValidateUserDeviceResponse { IsRegistered = true },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

        foreach (var (id, userId) in new[] { ("m-1", Caller), ("m-2", Callee), ("m-3", Bystander) })
        {
            _context.Members.Add(new ConversationMember
            {
                Id = id, UserId = userId, ConversationId = ConversationId,
                PublicKey = [], CachedUserName = userId, CachedUserHash = 0,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private VoiceController ControllerFor(string userId)
    {
        var callStore = new LockedJsonCacheStore(new FakeDistributedLockService(), _cache);

        // IceServerService is only touched by the ice-servers endpoint, and constructing one drags
        // in Cloudflare credentials - see VoicePendingCallTests.
        return new VoiceController(
            null!, _bus, _cache, callStore,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            TestPrivacyServices.Build(_bus).Policy,
            _viewers, _context,
            VoiceTestHarness.StoreFor(_cache, new FakeDistributedLockService()),
            VoiceTestHarness.ServiceFor(_cache, new FakeDistributedLockService(), _hub), _hub)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.ForUser(userId) },
            },
        };
    }

    private async Task SeedCallAsync(CallStatus status, CallStatus calleeStatus, string conversationId = ConversationId)
    {
        var call = new Call
        {
            Id = CallId,
            ConversationId = conversationId,
            CreatorId = Caller,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            Participants =
            [
                // Media handles deliberately absent: this fixture is about what a conversation
                // member outside the call is told, and OngoingCallDto has never carried them.
                new CallParticipant { UserId = Caller, Status = CallStatus.Connected },
                new CallParticipant { UserId = Callee, Status = calleeStatus },
            ],
        };
        await _cache.SetAsync(Call.GetCacheId(CallId), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(call)), new());

        // The viewer broadcast is addressed to the voice room, not to the Call's invitee list -
        // a viewer count is about media, and someone who is merely being rung is watching nothing.
        await VoiceTestHarness.SeedRoomAsync(_cache, new VoiceRoom
        {
            RoomId = CallId,
            Kind = VoiceRoomKind.Call,
            Participants = [new VoiceParticipant { UserId = Caller }, new VoiceParticipant { UserId = Callee }],
        });
    }

    private Task IndexAsync(string conversationId = ConversationId) =>
        _cache.SetStringAsync(CallService.ConversationCallKey(conversationId), CallId);

    // ══════════════════════════════════════════════════════════════════════════ Discovering an
    // ongoing call ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NoCall_IsNoContent()
    {
        var result = await ControllerFor(Bystander).GetConversationCall(ConversationId, CancellationToken.None);

        Assert.That(result, Is.TypeOf<NoContentResult>());
    }

    [Test]
    public async Task OngoingCall_IsVisibleToAMemberWhoIsNotInIt()
    {
        await SeedCallAsync(CallStatus.Connected, CallStatus.Connected);
        await IndexAsync();

        var result = await ControllerFor(Bystander).GetConversationCall(ConversationId, CancellationToken.None);

        var dto = (OngoingCallDto)((OkObjectResult)result).Value!;
        Assert.Multiple(() =>
        {
            Assert.That(dto.CallId, Is.EqualTo(CallId));
            Assert.That(dto.CreatorId, Is.EqualTo(Caller));
            Assert.That(dto.ConnectedUserIds, Is.EquivalentTo(new[] { Caller, Callee }));
        });
    }

    [Test]
    public async Task OngoingCall_NeverCarriesMediaHandles()
    {
        await SeedCallAsync(CallStatus.Connected, CallStatus.Connected);
        await IndexAsync();

        var result = await ControllerFor(Bystander).GetConversationCall(ConversationId, CancellationToken.None);

        var serialized = JsonSerializer.Serialize(((OkObjectResult)result).Value);
        Assert.Multiple(() =>
        {
            Assert.That(serialized, Does.Not.Contain("cf-caller"),
                "a session id is a capability over live media on a shared Cloudflare app");
            Assert.That(serialized, Does.Not.Contain("audioTrackName"));
        });
    }

    [Test]
    public async Task NonMember_IsNotToldAnything()
    {
        await SeedCallAsync(CallStatus.Connected, CallStatus.Connected);
        await IndexAsync();

        var result = await ControllerFor(Stranger).GetConversationCall(ConversationId, CancellationToken.None);

        Assert.That(result, Is.TypeOf<NotFoundResult>(),
            "not even the existence of the call - conversation membership gates the whole read");
    }

    [Test]
    public async Task IndexOutlivingItsCall_ReadsAsNoCall()
    {
        // Nothing deletes this index on every path a call can end, so the call itself is re-read
        // and re-checked.
        await IndexAsync();

        var result = await ControllerFor(Bystander).GetConversationCall(ConversationId, CancellationToken.None);

        Assert.That(result, Is.TypeOf<NoContentResult>());
    }

    [Test]
    public async Task CompletedCall_ReadsAsNoCall()
    {
        await SeedCallAsync(CallStatus.Completed, CallStatus.Left);
        await IndexAsync();

        var result = await ControllerFor(Bystander).GetConversationCall(ConversationId, CancellationToken.None);

        Assert.That(result, Is.TypeOf<NoContentResult>());
    }

    [Test]
    public async Task StillRingingCall_IsAlreadyVisible()
    {
        // Pending, not Connected: the caller is on it and it is ringing the callee.
        await SeedCallAsync(CallStatus.Pending, CallStatus.Pending);
        await IndexAsync();

        var result = await ControllerFor(Bystander).GetConversationCall(ConversationId, CancellationToken.None);

        Assert.That(result, Is.TypeOf<OkObjectResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════ Screen share
    // viewers ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Watch_CountsTheCallerAndTellsTheCall()
    {
        await SeedCallAsync(CallStatus.Connected, CallStatus.Connected);

        var result = await ControllerFor(Callee).WatchCallShare(CallId, "share-1", CancellationToken.None);

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        Assert.Multiple(() =>
        {
            Assert.That(JsonSerializer.Serialize(((OkObjectResult)result).Value), Does.Contain("\"viewerCount\":1"));
            Assert.That(HubClients.SentMessages.Any(m => m.Method == "call.ShareViewersChanged"), Is.True);
        });
    }

    [Test]
    public async Task Unwatch_StopsCountingThem()
    {
        await SeedCallAsync(CallStatus.Connected, CallStatus.Connected);
        var controller = ControllerFor(Callee);
        await controller.WatchCallShare(CallId, "share-1", CancellationToken.None);

        var result = await controller.UnwatchCallShare(CallId, "share-1", CancellationToken.None);

        Assert.That(JsonSerializer.Serialize(((OkObjectResult)result).Value), Does.Contain("\"viewerCount\":0"));
    }

    [Test]
    public async Task Watch_ByAnInviteeWhoNeverAnswered_IsRefused()
    {
        // Media only flows to someone actually in the call.
        await SeedCallAsync(CallStatus.Pending, CallStatus.Pending);

        var result = await ControllerFor(Callee).WatchCallShare(CallId, "share-1", CancellationToken.None);

        Assert.That(result, Is.TypeOf<ForbidResult>());
    }

    [Test]
    public async Task Watch_ByANonParticipant_IsRefused()
    {
        await SeedCallAsync(CallStatus.Connected, CallStatus.Connected);

        var result = await ControllerFor(Bystander).WatchCallShare(CallId, "share-1", CancellationToken.None);

        Assert.That(result, Is.TypeOf<ForbidResult>());
    }

    [Test]
    public async Task Watch_OfAnUnknownCall_IsNotFound()
    {
        var result = await ControllerFor(Callee).WatchCallShare("call-missing", "share-1", CancellationToken.None);

        Assert.That(result, Is.TypeOf<NotFoundResult>());
    }

    [Test]
    public async Task Viewers_AreReadableByParticipants()
    {
        await SeedCallAsync(CallStatus.Connected, CallStatus.Connected);
        var controller = ControllerFor(Callee);
        await controller.WatchCallShare(CallId, "share-1", CancellationToken.None);

        var result = await controller.GetCallShareViewers(CallId, CancellationToken.None);

        var viewers = (Dictionary<string, IReadOnlyList<string>>)((OkObjectResult)result).Value!;
        Assert.That(viewers["share-1"], Is.EqualTo(new[] { Callee }));
    }

    [Test]
    public async Task Viewers_AreNotReadableByANonParticipant()
    {
        await SeedCallAsync(CallStatus.Connected, CallStatus.Connected);

        var result = await ControllerFor(Bystander).GetCallShareViewers(CallId, CancellationToken.None);

        Assert.That(result, Is.TypeOf<NotFoundResult>());
    }
}
