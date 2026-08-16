using System.Text.Json;
using System.Text.Json.Nodes;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Sources;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
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

/// <summary>A call join that was reduced says so on the reply that caused it.</summary>
[TestFixture]
public class CallJoinDegradationTests
{
    private const string CallId = "call-1";
    private const string First = "user-first";
    private const string Second = "user-second";

    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;
    private LockedJsonCacheStore _callStore = null!;
    private FakeMessageBus _bus = null!;

    /// <summary>One seat, so the second joiner is over capacity.</summary>
    private static readonly OperatorCeilings OneSeat = new(
        new Dictionary<EntitlementKey, EntitlementValue>
        {
            [EntitlementKeys.VoiceMaxParticipants] = EntitlementValue.OfNumber(1),
        });

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _hub = new FakeMessagingHubContext();
        _callStore = new LockedJsonCacheStore(new FakeDistributedLockService(), _cache);
        _bus = new FakeMessageBus(msg => msg switch
        {
            ValidateUserDeviceRequest => new ValidateUserDeviceResponse { IsRegistered = true },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

        _cache.SetEntry(Call.GetCacheId(CallId), JsonSerializer.Serialize(new Call
        {
            Id = CallId,
            ConversationId = "conv-1",
            CreatorId = First,
            Status = CallStatus.Pending,
            Participants =
            [
                new CallParticipant { UserId = First },
                new CallParticipant { UserId = Second },
            ],
        }));
    }

    private CallVoiceMediaController ControllerFor(string userId, OperatorCeilings? ceilings)
    {
        var http = new DefaultHttpContext { User = TestPrincipal.ForUser(userId) };
        http.Request.Headers[DeviceIdentity.HeaderName] = $"device-{userId}";
        var locks = new FakeDistributedLockService();

        return new CallVoiceMediaController(
            new FakeVoiceSfu(), _cache, _callStore, _bus,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            new VoiceRoomService(
                VoiceTestHarness.StoreFor(_cache, locks), new VoiceAnnouncer(_hub),
                operatorCeilings: ceilings),
            VoiceTestHarness.StoreFor(_cache, locks),
            NullLogger<CallVoiceMediaController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private async Task<object?> JoinAsync(string userId, OperatorCeilings? ceilings) =>
        ((OkObjectResult)await ControllerFor(userId, ceilings)
            .CreateConnection(CallId, CancellationToken.None)).Value;

    [Test]
    public async Task The_joiner_past_the_ceiling_is_admitted_and_told_what_it_cost()
    {
        await JoinAsync(First, OneSeat);

        var body = await JoinAsync(Second, OneSeat) as JsonObject;

        Assert.Multiple(async () =>
        {
            Assert.That(body, Is.Not.Null,
                "a reduced join is a 200 carrying the normal body plus the array - an error status "
                + "would make the client roll back, which is a denial with extra steps");
            Assert.That(body!["mediaSessionId"], Is.Not.Null, "and the normal body is still all there");

            var degradations = body["degradations"]!.AsArray();
            Assert.That(degradations, Has.Count.EqualTo(1));
            Assert.That((string?)degradations[0]!["key"],
                Is.EqualTo(EntitlementKeys.VoiceMaxParticipants.Name));
            Assert.That((string?)degradations[0]!["reason"], Is.EqualTo("operator_ceiling"));
            Assert.That((string?)degradations[0]!["remedy"], Is.EqualTo("none"),
                "no amount of money moves an operator ceiling, so an upgrade link against one sells "
                + "a change that would not happen");

            var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Call(CallId));
            Assert.That(room!.Participants.Select(p => p.UserId), Does.Contain(Second),
                "degrade, do not deny - they are in the call either way");
        });
    }

    [Test]
    public async Task A_join_inside_the_ceiling_is_byte_identical_to_what_a_v1_client_receives()
    {
        var body = await JoinAsync(First, OneSeat);

        Assert.That(body, Is.Not.InstanceOf<JsonObject>(),
            "absent and empty mean the same thing to a client, and absent is what the reply has "
            + "always looked like");
    }

    [Test]
    public async Task A_box_with_no_ceilings_configured_never_reduces_anybody()
    {
        await JoinAsync(First, null);

        var body = await JoinAsync(Second, null);

        Assert.That(body, Is.Not.InstanceOf<JsonObject>(),
            "which is every deployment that has not set one, and a call has no plan of its own to "
            + "bind it");
    }
}
