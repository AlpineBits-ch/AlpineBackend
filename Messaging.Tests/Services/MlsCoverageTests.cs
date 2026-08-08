using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;

using static Messaging.Tests.Helpers.TestMlsServices;

namespace Messaging.Tests.Services;

/// <summary>Reading device coverage after the fact.</summary>
[TestFixture]
public class MlsCoverageTests
{
    private const string ConversationId = "conv-1";
    private const string ChannelId = "chan-1";
    private const string CallerId = "user-caller";
    private const string PeerId = "user-peer";

    private const string CallerPhone = "device-caller-phone";
    private const string CallerLaptop = "device-caller-laptop";

    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private TestMessagingContext _context = null!;
    private FakeMessagingHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _hub = new FakeMessagingHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>A service whose Identity answers with the given devices, keyed by user.</summary>
    private MlsGroupService ServiceKnowing(params (string UserId, string DeviceId)[] devices)
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetUserDevicesRequest r => new GetUserDevicesResponse
            {
                Devices = devices
                    .Where(d => r.UserIds.Contains(d.UserId))
                    .Select(d => new UserDeviceSummaryResponse
                    {
                        UserId = d.UserId, ClientDeviceId = d.DeviceId, DeviceName = d.DeviceId,
                    })
                    .ToList(),
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        return Service(bus);
    }

    /// <summary>A service whose Identity is down.</summary>
    private MlsGroupService ServiceWithoutIdentity() => Service(new FakeMessageBus());

    private MlsGroupService Service(FakeMessageBus bus) =>
        new(_context, _hub, bus, new MlsJoinRequestService(_context), Coverage(bus));

    private async Task SeedConversation(params string[] memberIds)
    {
        _context.Conversations.Add(new Conversation
        {
            Id = ConversationId,
            CreatedAt = T0,
            UpdatedAt = T0,
            EncryptionState = ChannelEncryptionState.Plain,
            Members = memberIds.Select((u, i) => new ConversationMember
            {
                Id = $"m-{i}", UserId = u, ConversationId = ConversationId, PublicKey = [],
                CachedUserName = u, CachedUserHash = 0, CreatedAt = T0, UpdatedAt = T0,
            }).ToList(),
        });
        await _context.SaveChangesAsync();
    }

    private static EnableMlsDto EnableDto(params DeviceWelcomeDto[] welcomes) => new()
    {
        MlsGroupId = [1, 2, 3],
        MlsGroupInfo = [4, 5, 6],
        Epoch = 0,
        Welcomes = welcomes.ToList(),
    };

    private static DeviceWelcomeDto Welcome(string userId, string deviceId) =>
        new() { UserId = userId, DeviceId = deviceId, Welcome = [7] };

    private static Task<MlsCoverageDto> ReadCoverage(MlsGroupService service) =>
        service.GetCoverageAsync(ConversationId, ConversationId, CallerId);

    // ══════════════════════════════════════════════════════════════════════════ The caller's own
    // devices ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Coverage_NamesTheCallersOwnDeviceThatGotNoWelcome()
    {
        // The whole point: a device you own that cannot read a conversation you are in, said out
        // loud, at a moment of your choosing rather than only in a response you have long closed.
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing((CallerId, CallerPhone), (CallerId, CallerLaptop));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(Welcome(CallerId, CallerLaptop)), T0, activatingDeviceId: CallerPhone);

        var coverage = await ReadCoverage(service);

        Assert.That(coverage.Encrypted, Is.True);
        Assert.That(coverage.Generation, Is.EqualTo(1));
        Assert.That(coverage.OwnDevices.Select(d => (d.DeviceId, d.Covered)), Is.EquivalentTo(new[]
        {
            (CallerLaptop, true),
            (CallerPhone, true),
        }));
    }

    [Test]
    public async Task Coverage_ADeviceWithNoTraceOfTheGroup_ReadsAsUncovered()
    {
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing((CallerId, CallerPhone), (CallerId, CallerLaptop));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(), T0, activatingDeviceId: CallerPhone);

        var coverage = await ReadCoverage(service);

        Assert.That(coverage.OwnDevices.Single(d => d.DeviceId == CallerLaptop).Covered, Is.False);
    }

    [Test]
    public async Task Coverage_TheDeviceThatBuiltTheGroup_IsNeverAccused()
    {
        // It holds the group directly and has no Welcome addressed to it by construction.
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing((CallerId, CallerPhone));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(), T0, activatingDeviceId: CallerPhone);

        var coverage = await ReadCoverage(service);

        Assert.That(coverage.OwnDevices.Single().Covered, Is.True);
    }

    [Test]
    public async Task Coverage_OnAGenerationMintedBeforeTheDeviceWasRecorded_FallsBackToUncovered()
    {
        // Rows written before the column existed have no activating device, and null means "we do
        // not know" rather than "no device".
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing((CallerId, CallerPhone));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId, EnableDto(), T0);

        var coverage = await ReadCoverage(service);

        Assert.That(coverage.OwnDevices.Single().Covered, Is.False);
    }

    [Test]
    public async Task Coverage_ADeviceThatPublishedACommit_ReadsAsCovered()
    {
        // You cannot commit to a group you are not in, so a commit is proof of a leaf even when the
        // Welcome that admitted the device is not this generation's.
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing((CallerId, CallerPhone), (CallerId, CallerLaptop));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(), T0, activatingDeviceId: CallerPhone);

        await service.PublishCommitAsync(ConversationId, ConversationId, null, CallerId,
            new PublishMlsCommitDto
            {
                Epoch = 1,
                Commit = MlsWire.Commit(),
                SenderDeviceId = CallerLaptop,
            }, [], T0);

        var coverage = await ReadCoverage(service);

        Assert.That(coverage.OwnDevices.Single(d => d.DeviceId == CallerLaptop).Covered, Is.True);
    }

    [Test]
    public async Task Coverage_AWelcomeTheDeviceHasNotAcknowledged_StillCounts()
    {
        // The Welcome is parked and addressed to that device; it has been handed its way in.
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing((CallerId, CallerPhone), (CallerId, CallerLaptop));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(Welcome(CallerId, CallerLaptop)), T0, activatingDeviceId: CallerPhone);

        var coverage = await ReadCoverage(service);

        Assert.That(_context.PendingWelcomes.Single().ConsumedAt, Is.Null);
        Assert.That(coverage.OwnDevices.Single(d => d.DeviceId == CallerLaptop).Covered, Is.True);
    }

    [Test]
    public async Task Coverage_IsAnsweredForTheLiveGenerationOnly()
    {
        // A Welcome into generation 1 says nothing about generation 2: re-keying mints a genuinely
        // new group, and a device holding only the old one reads the new group's messages as noise.
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing((CallerId, CallerPhone), (CallerId, CallerLaptop));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(Welcome(CallerId, CallerLaptop)), T0, activatingDeviceId: CallerPhone);

        var afterCooldown = T0 + MlsGroupService.ToggleCooldown + TimeSpan.FromSeconds(1);
        await service.DisableAsync(ConversationId, ConversationId, null, CallerId, afterCooldown);

        var rekeyed = afterCooldown + MlsGroupService.ToggleCooldown + TimeSpan.FromSeconds(1);
        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(), rekeyed, activatingDeviceId: CallerPhone);

        var coverage = await ReadCoverage(service);

        Assert.That(coverage.Generation, Is.EqualTo(2));
        Assert.That(coverage.OwnDevices.Single(d => d.DeviceId == CallerLaptop).Covered, Is.False,
            "a leaf in the old group is not a leaf in the new one");
    }

    // ══════════════════════════════════════════════════════════════════════════ Everyone else
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Coverage_NamesAPeersUncoveredDevice()
    {
        // Their stranded handset is your problem too - it is the reason your messages arrive to
        // silence on one of their screens.
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing(
            (CallerId, CallerPhone),
            (PeerId, "device-peer-phone"),
            (PeerId, "device-peer-tablet"));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(Welcome(PeerId, "device-peer-phone")), T0, activatingDeviceId: CallerPhone);

        var coverage = await ReadCoverage(service);

        Assert.That(coverage.UnreachableDevices.Select(d => (d.UserId, d.DeviceId)),
            Is.EquivalentTo(new[] { (PeerId, "device-peer-tablet") }));
    }

    [Test]
    public async Task Coverage_DoesNotListAPeersWorkingDevices()
    {
        // The uncovered ones are already reported by the write paths, so naming them again
        // discloses nothing new.
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing(
            (CallerId, CallerPhone),
            (PeerId, "device-peer-phone"));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(Welcome(PeerId, "device-peer-phone")), T0, activatingDeviceId: CallerPhone);

        var coverage = await ReadCoverage(service);

        Assert.That(coverage.UnreachableDevices, Is.Empty);
        Assert.That(coverage.OwnDevices.Select(d => d.DeviceId), Is.EquivalentTo(new[] { CallerPhone }));
    }

    [Test]
    public async Task Coverage_NeverReportsTheCallersOwnDeviceTwice()
    {
        // Own devices carry their own verdict.
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing((CallerId, CallerPhone), (CallerId, CallerLaptop));

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(), T0, activatingDeviceId: CallerPhone);

        var coverage = await ReadCoverage(service);

        Assert.That(coverage.OwnDevices.Single(d => d.DeviceId == CallerLaptop).Covered, Is.False);
        Assert.That(coverage.UnreachableDevices, Is.Empty);
    }

    [Test]
    public async Task Coverage_ForAChannel_AnswersForTheCallerOnly()
    {
        // A channel's roster lives in Guild, so there is no membership here to enumerate other
        // people's devices from - and enumerating a guild to answer a diagnostic would make this a
        // directory of every device in the server.
        var service = ServiceKnowing(
            (CallerId, CallerPhone),
            (PeerId, "device-peer-phone"));

        await service.EnableAsync(ChannelId, null, ChannelId, CallerId,
            EnableDto(), T0, activatingDeviceId: CallerPhone);

        var coverage = await service.GetCoverageAsync(ChannelId, null, CallerId);

        Assert.That(coverage.OwnDevices.Select(d => d.DeviceId), Is.EquivalentTo(new[] { CallerPhone }));
        Assert.That(coverage.UnreachableDevices, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ Answers that are
    // not answers ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Coverage_OnAPlaintextConversation_ReportsNoGroupRatherThanNoDevices()
    {
        // Empty lists here mean "there is nothing to be outside of".
        await SeedConversation(CallerId, PeerId);
        var service = ServiceKnowing((CallerId, CallerPhone), (CallerId, CallerLaptop));

        var coverage = await ReadCoverage(service);

        Assert.Multiple(() =>
        {
            Assert.That(coverage.Encrypted, Is.False);
            Assert.That(coverage.Generation, Is.Null);
            Assert.That(coverage.OwnDevices, Is.Empty);
            Assert.That(coverage.UnreachableDevices, Is.Empty);
            Assert.That(coverage.CoverageUnavailable, Is.False);
        });
    }

    [Test]
    public async Task Coverage_WhenIdentityIsDown_SaysSoInsteadOfAllClear()
    {
        // Answering "no devices are stranded" because the device list could not be read is the
        // exact silence this route exists to break.
        await SeedConversation(CallerId, PeerId);
        var service = ServiceWithoutIdentity();

        await service.EnableAsync(ConversationId, ConversationId, null, CallerId,
            EnableDto(), T0, activatingDeviceId: CallerPhone);

        var coverage = await ReadCoverage(service);

        Assert.Multiple(() =>
        {
            Assert.That(coverage.Encrypted, Is.True);
            Assert.That(coverage.CoverageUnavailable, Is.True);
            Assert.That(coverage.OwnDevices, Is.Empty);
            Assert.That(coverage.UnreachableDevices, Is.Empty);
        });
    }
}
