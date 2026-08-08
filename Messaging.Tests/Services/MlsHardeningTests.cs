using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Events;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

using static Messaging.Tests.Helpers.TestMlsServices;

namespace Messaging.Tests.Services;

/// <summary>The MLS hardening fixes, each pinned by the failure it prevents.</summary>
[TestFixture]
public class MlsHardeningTests
{
    private const string ChannelId = "chan-1";
    private const string ConversationId = "conv-1";
    private const string AdminId = "user-admin";
    private const string MemberId = "user-member";

    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private TestMessagingContext _context = null!;
    private FakeMessagingHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private MlsGroupService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _hub = new FakeMessagingHubContext();
        _bus = new FakeMessageBus();
        _service = new MlsGroupService(_context, _hub, _bus, new MlsJoinRequestService(_context), Coverage(_bus));
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private FakeHubClients Sent => (FakeHubClients)_hub.Clients;

    private static EnableMlsDto EnableDto(long epoch = 0, params DeviceWelcomeDto[] welcomes) => new()
    {
        MlsGroupId = [1, 2, 3],
        MlsGroupInfo = [4, 5, 6],
        Epoch = epoch,
        Welcomes = welcomes.ToList(),
    };

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

    // ══════════════════════════════════════════════════════════════════════════
    // E-M1 - the early return that Wolverine would have committed anyway
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Enable_ForAMissingConversation_LeavesNoGenerationBehind()
    {
        var result = await _service.EnableAsync("conv-missing", "conv-missing", null, AdminId, EnableDto(), T0);

        // The NotFound used to be taken after the generation row was added to the context.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.NotFound));
        Assert.That(_context.ChangeTracker.Entries<MlsGroupGeneration>().Any(), Is.False,
            "A refused enable must leave nothing for the transactional middleware to commit");
        Assert.That(await _context.MlsGroupGenerations.AnyAsync(), Is.False);
    }

    [Test]
    public async Task Disable_ForAMissingConversation_LeavesTheGenerationActive()
    {
        // A generation whose ConversationId points at a row that has since gone.
        _context.MlsGroupGenerations.Add(MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
        {
            ContextId = "conv-missing",
            ConversationId = "conv-missing",
            Generation = 1,
            MlsGroupId = [1],
            Epoch = 0,
            ActivatedByUserId = AdminId,
            ActivatedAt = T0,
        }));
        await _context.SaveChangesAsync();

        var result = await _service.DisableAsync(
            "conv-missing", "conv-missing", null, AdminId, T0 + TimeSpan.FromMinutes(5));

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.NotFound));

        // Terminating and then returning NotFound left the context with no active generation and no
        // route back to one - the toggle cooldown blocks re-enabling, and nothing reports why.
        var generation = await _context.MlsGroupGenerations.SingleAsync();
        Assert.That(generation.State, Is.EqualTo(MlsGenerationState.Active));
        Assert.That(generation.TerminatedAt, Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // E4 - a commit whose response was lost must be recoverable
    // ══════════════════════════════════════════════════════════════════════════

    private Task<MlsOperationResult> PublishChannel(
        long epoch, byte[]? commit = null, string device = "device-a", bool isProposal = false,
        DateTimeOffset? at = null) =>
        _service.PublishCommitAsync(ChannelId, null, ChannelId, AdminId, new PublishMlsCommitDto
        {
            Epoch = epoch,
            // Real MLSMessage framing, because the server now checks that the declared kind matches
            // the payload's - see MlsMessageInspector.
            Commit = commit ?? (isProposal ? MlsWire.Proposal() : MlsWire.Commit()),
            SenderDeviceId = device,
            IsProposal = isProposal,
        }, [], at ?? T0);

    [Test]
    public async Task PublishCommit_RepublishedByTheSameDevice_SucceedsInsteadOfConflicting()
    {
        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(), T0);
        await PublishChannel(epoch: 1);

        var replay = await PublishChannel(epoch: 1);

        // The client merged locally and never saw the response.
        Assert.That(replay.Status, Is.EqualTo(MlsOperationStatus.Ok));
        var payload = (MlsCommitPublishedDto)replay.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(payload.Duplicate, Is.True);
            Assert.That(payload.Epoch, Is.EqualTo(1));
        });
        Assert.That(await _context.MlsCommits.CountAsync(), Is.EqualTo(1), "No second row is written");
    }

    [Test]
    public async Task PublishCommit_DifferentBytesAtTheSameEpoch_IsStillAConflict()
    {
        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(), T0);
        await PublishChannel(epoch: 1, commit: MlsWire.Commit(epoch: 0));

        var result = await PublishChannel(epoch: 1, commit: MlsWire.Commit(epoch: 99));

        // Idempotency is matched on the exact payload for a reason: two different commits at one
        // epoch is a genuine fork, not a retry, and treating it as one would silently drop a change.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
        Assert.That(result.Value, Is.InstanceOf<MlsEpochConflictDto>());
    }

    [Test]
    public async Task PublishCommit_SameEpochFromAnotherDevice_IsAConflict()
    {
        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(), T0);
        await PublishChannel(epoch: 1, device: "device-a");

        var result = await PublishChannel(epoch: 1, device: "device-b");

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // A device left outside the group must not be left there in silence
    // ══════════════════════════════════════════════════════════════════════════

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

        return new MlsGroupService(_context, _hub, bus, new MlsJoinRequestService(_context), Coverage(bus));
    }

    [Test]
    public async Task PublishCommit_AddingSomeoneWhoseSecondDeviceGotNoWelcome_NamesThatDevice()
    {
        // Adding somebody to an encrypted conversation is a roster row and then an Add commit
        // carrying one Welcome per device, and the publisher builds that list from whatever
        // /consume-tokens gave back.
        await SeedConversation(AdminId, MemberId);
        var service = ServiceKnowing(
            (MemberId, "device-member-phone"),
            (MemberId, "device-member-desktop"));

        await service.EnableAsync(ConversationId, ConversationId, null, AdminId, EnableDto(), T0);

        var result = await service.PublishCommitAsync(ConversationId, ConversationId, null, AdminId,
            new PublishMlsCommitDto
            {
                Epoch = 1,
                Commit = MlsWire.Commit(),
                SenderDeviceId = "device-admin",
                Welcomes =
                [
                    new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-member-phone", Welcome = [7] },
                ],
            }, [], T0);

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
        var payload = (MlsCommitPublishedDto)result.Value!;
        Assert.That(payload.UnreachableDevices.Select(d => (d.UserId, d.DeviceId)),
            Is.EquivalentTo(new[] { (MemberId, "device-member-desktop") }));
    }

    [Test]
    public async Task PublishCommit_CarryingNoWelcomes_AccusesNobody()
    {
        // An update or a removal admits nobody, so there is no coverage question to ask.
        await SeedConversation(AdminId, MemberId);
        var service = ServiceKnowing(
            (MemberId, "device-member-phone"),
            (MemberId, "device-member-desktop"));

        await service.EnableAsync(ConversationId, ConversationId, null, AdminId, EnableDto(), T0);

        var result = await service.PublishCommitAsync(ConversationId, ConversationId, null, AdminId,
            new PublishMlsCommitDto
            {
                Epoch = 1,
                Commit = MlsWire.Commit(),
                SenderDeviceId = "device-admin",
            }, [], T0);

        Assert.That(((MlsCommitPublishedDto)result.Value!).UnreachableDevices, Is.Empty);
    }

    [Test]
    public async Task PublishCommit_ADeviceAlreadyWelcomedEarlierInTheGeneration_IsNotReportedAgain()
    {
        // Already in the group needs no new Welcome.
        await SeedConversation(AdminId, MemberId);
        var service = ServiceKnowing(
            (MemberId, "device-member-phone"),
            (MemberId, "device-member-desktop"));

        await service.EnableAsync(ConversationId, ConversationId, null, AdminId,
            EnableDto(0, new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-member-desktop", Welcome = [1] }),
            T0);

        var result = await service.PublishCommitAsync(ConversationId, ConversationId, null, AdminId,
            new PublishMlsCommitDto
            {
                Epoch = 1,
                Commit = MlsWire.Commit(),
                SenderDeviceId = "device-admin",
                Welcomes =
                [
                    new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-member-phone", Welcome = [7] },
                ],
            }, [], T0);

        Assert.That(((MlsCommitPublishedDto)result.Value!).UnreachableDevices, Is.Empty);
    }

    [Test]
    public async Task Enable_ForAConversation_ReportsMemberDevicesTheNewGenerationLeftBehind()
    {
        // Enabling or re-keying mints a group only the welcomed devices will ever hold.
        await SeedConversation(AdminId, MemberId);
        var service = ServiceKnowing(
            (MemberId, "device-member-phone"),
            (MemberId, "device-member-desktop"));

        var result = await service.EnableAsync(ConversationId, ConversationId, null, AdminId,
            EnableDto(0, new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-member-phone", Welcome = [1] }),
            T0);

        var payload = (MlsToggleResultDto)result.Value!;
        Assert.That(payload.UnreachableDevices.Select(d => (d.UserId, d.DeviceId)),
            Is.EquivalentTo(new[] { (MemberId, "device-member-desktop") }));
    }

    [Test]
    public async Task Enable_FromOneOfYourOwnDevices_ReportsTheOthersAndNotTheOneAsking()
    {
        // Re-keying from your phone leaves your laptop outside the new group exactly as it leaves a
        // member's second handset outside it, and only the second was ever mentioned: the enabling
        // user was skipped wholesale, because the device holding the group it just built has no
        // Welcome addressed to it and would have been reported as unable to read the context it
        // created. Naming that device is what lets the false alarm go without the true one.
        await SeedConversation(AdminId, MemberId);
        var service = ServiceKnowing(
            (AdminId, "device-admin-phone"),
            (AdminId, "device-admin-laptop"));

        var result = await service.EnableAsync(
            ConversationId, ConversationId, null, AdminId, EnableDto(), T0,
            activatingDeviceId: "device-admin-phone");

        var payload = (MlsToggleResultDto)result.Value!;
        Assert.That(payload.UnreachableDevices.Select(d => (d.UserId, d.DeviceId)),
            Is.EquivalentTo(new[] { (AdminId, "device-admin-laptop") }));
    }

    [Test]
    public async Task Enable_WithNoDeviceHeader_StillSaysNothingAboutTheEnablersOwnDevices()
    {
        // With no header there is no way to tell which device is asking, so a report naming the
        // caller's devices would name the one that built the group among them.
        await SeedConversation(AdminId, MemberId);
        var service = ServiceKnowing(
            (AdminId, "device-admin-phone"),
            (AdminId, "device-admin-laptop"));

        var result = await service.EnableAsync(
            ConversationId, ConversationId, null, AdminId, EnableDto(), T0);

        Assert.That(((MlsToggleResultDto)result.Value!).UnreachableDevices, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ E2 - a proposal is
    // not a commit ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishProposal_DoesNotAdvanceTheEpoch()
    {
        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(epoch: 3), T0);

        var result = await PublishChannel(epoch: 4, isProposal: true);

        // Processing a proposal advances no client's MLS epoch.
        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
        Assert.That((await _context.MlsGroupGenerations.SingleAsync()).Epoch, Is.EqualTo(3),
            "A proposal must leave the group exactly where it was");
    }

    [Test]
    public async Task PublishProposal_DoesNotBlockTheRealCommitAtThatEpoch()
    {
        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(epoch: 3), T0);
        await PublishChannel(epoch: 4, commit: MlsWire.Proposal(epoch: 1), isProposal: true);

        var real = await PublishChannel(epoch: 4, commit: MlsWire.Commit(epoch: 2));

        // A proposal announced at N+1 must not consume the epoch slot the commit that actually
        // establishes N+1 needs, or the group can never move again.
        Assert.That(real.Status, Is.EqualTo(MlsOperationStatus.Ok));
        Assert.That((await _context.MlsGroupGenerations.SingleAsync()).Epoch, Is.EqualTo(4));
        Assert.That(await _context.MlsCommits.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetCommits_MarksProposalsSoClientsDoNotCountThem()
    {
        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(epoch: 0), T0);
        await PublishChannel(epoch: 1, isProposal: true);

        var commits = await _service.GetCommitsAsync(ChannelId, generation: null, sinceEpoch: 0);

        Assert.That(commits.Single().IsProposal, Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // E10 - generations must never interleave
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetCommits_WithNoActiveGeneration_ReturnsOnlyTheMostRecentOne()
    {
        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(epoch: 0), T0);
        await PublishChannel(epoch: 1, commit: MlsWire.Commit(epoch: 1), at: T0);
        var afterCooldown = T0 + MlsGroupService.ToggleCooldown + TimeSpan.FromSeconds(1);
        await _service.DisableAsync(ChannelId, null, ChannelId, AdminId, afterCooldown);

        var later = afterCooldown + MlsGroupService.ToggleCooldown + TimeSpan.FromSeconds(1);
        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(epoch: 0), later);
        await PublishChannel(epoch: 1, commit: MlsWire.Commit(epoch: 2), at: later);
        var evenLater = later + MlsGroupService.ToggleCooldown + TimeSpan.FromSeconds(1);
        await _service.DisableAsync(ChannelId, null, ChannelId, AdminId, evenLater);

        // Nothing is live now.
        var commits = await _service.GetCommitsAsync(ChannelId, generation: null, sinceEpoch: 0);

        Assert.That(commits.Select(c => c.Generation), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public async Task GetCommits_OnANeverEncryptedContext_ReturnsNothing()
    {
        Assert.That(await _service.GetCommitsAsync("chan-never", generation: null, sinceEpoch: 0), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // E-H8 - retention must not strand a device that has not joined yet
    // ══════════════════════════════════════════════════════════════════════════

    private void SeedAgedCommit(long epoch, DateTimeOffset createdAt, int generation = 1)
    {
        var commit = MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = ChannelId,
            ChannelId = ChannelId,
            Generation = generation,
            Epoch = epoch,
            Commit = [(byte)epoch],
            SenderUserId = AdminId,
            SenderDeviceId = "device-a",
        });
        commit.CreatedAt = createdAt;
        _context.MlsCommits.Add(commit);
    }

    [Test]
    public async Task Retention_KeepsCommitsAboveAnUnconsumedWelcome()
    {
        var now = T0;
        var ancient = now - MlsGroupService.CommitRetention - TimeSpan.FromDays(1);

        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(epoch: 7), now);
        SeedAgedCommit(epoch: 5, createdAt: ancient);
        SeedAgedCommit(epoch: 6, createdAt: ancient);
        SeedAgedCommit(epoch: 7, createdAt: ancient);

        // A device was handed a Welcome landing it at epoch 5 and has not joined yet.
        _context.PendingWelcomes.Add(PendingWelcome.Create(new CreatePendingWelcomeParams
        {
            ContextId = ChannelId, ChannelId = ChannelId, UserId = MemberId, DeviceId = "device-late",
            Welcome = [1], Generation = 1, Epoch = 5,
        }));
        await _context.SaveChangesAsync();

        await PublishChannel(epoch: 8, at: now);

        // Pruning below the joiner's starting epoch hands it an empty catch-up list, which is
        // indistinguishable from "you are up to date" - so it believes it is in sync with a group it
        // cannot read. Epoch 5 is at the floor and safe to drop; 6 and 7 are what it still needs.
        var remaining = await _context.MlsCommits.OrderBy(c => c.Epoch).Select(c => c.Epoch).ToListAsync();
        Assert.That(remaining, Is.EqualTo(new long[] { 6, 7, 8 }));
    }

    [Test]
    public async Task Retention_PrunesFreelyOnceEveryWelcomeIsAcknowledged()
    {
        var now = T0;
        var ancient = now - MlsGroupService.CommitRetention - TimeSpan.FromDays(1);

        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(epoch: 7), now);
        SeedAgedCommit(epoch: 5, createdAt: ancient);
        SeedAgedCommit(epoch: 6, createdAt: ancient);

        var welcome = PendingWelcome.Create(new CreatePendingWelcomeParams
        {
            ContextId = ChannelId, ChannelId = ChannelId, UserId = MemberId, DeviceId = "device-late",
            Welcome = [1], Generation = 1, Epoch = 5,
        });
        welcome.ConsumedAt = now;
        _context.PendingWelcomes.Add(welcome);
        await _context.SaveChangesAsync();

        await PublishChannel(epoch: 8, at: now);

        Assert.That(await _context.MlsCommits.Select(c => c.Epoch).ToListAsync(), Is.EqualTo(new long[] { 8 }));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // E-M2 - the publisher does not get to choose arbitrary recipients
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task StoreWelcomes_ForAConversation_IgnoresNonMembers()
    {
        await SeedConversation(AdminId, MemberId);

        await _service.EnableAsync(ConversationId, ConversationId, null, AdminId, EnableDto(0,
            new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-ok", Welcome = [1] },
            new DeviceWelcomeDto { UserId = "outsider", DeviceId = "device-bad", Welcome = [2] }), T0);

        // The publisher picks the recipients, so without this any member could write unbounded rows
        // addressed at arbitrary user ids.
        var welcomes = await _context.PendingWelcomes.Select(w => w.UserId).ToListAsync();
        Assert.That(welcomes, Is.EqualTo(new[] { MemberId }));
    }

    [Test]
    public async Task StoreWelcomes_OverTheCap_IsRejectedOutright()
    {
        await SeedConversation(AdminId);

        var dto = EnableDto();
        dto.Welcomes = Enumerable.Range(0, MlsGroupService.MaxWelcomesPerCall + 1)
            .Select(i => new DeviceWelcomeDto { UserId = AdminId, DeviceId = $"d{i}", Welcome = [1] })
            .ToList();

        var result = await _service.EnableAsync(ConversationId, ConversationId, null, AdminId, dto, T0);

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.BadRequest));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // E8 / P0-7 - a channel commit used to be announced to nobody at all
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishChannelCommit_AnnouncesToTheChannelOverTheBus()
    {
        await _service.EnableAsync(ChannelId, null, ChannelId, AdminId, EnableDto(epoch: 0), T0);

        await PublishChannel(epoch: 1);

        // The endpoint passed an empty notify list and pointed at a "sibling path inside the
        // service" that did not exist, so a channel commit reached exactly the device that
        // published it.
        var announced = _bus.Published.OfType<ChannelMlsCommitPublished>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(announced.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(announced.Epoch, Is.EqualTo(1));
            Assert.That(announced.Generation, Is.EqualTo(1));
            Assert.That(announced.SenderDeviceId, Is.EqualTo("device-a"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Auto-admission is silent admission, never silent notification
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<string> SeedPendingJoinRequest(bool requiresManualApproval, int approvals = 0)
    {
        var request = MlsJoinRequest.Create(new CreateMlsJoinRequestParams
        {
            ContextId = ConversationId,
            ConversationId = ConversationId,
            Generation = 1,
            RequesterUserId = MemberId,
            RequesterDeviceId = "device-new",
            KeyPackage = [1],
            KeyPackageHash = "hash",
            SignatureKeyFingerprint = "AAAA-BBBB",
            CreatedAt = T0,
            ExpiresAt = T0 + MlsJoinRequest.Lifetime,
            RequiresManualApproval = requiresManualApproval,
        });

        for (var i = 0; i < approvals; i++)
            request.Approvals.Add(MlsJoinRequestApproval.Create(request.Id, $"approver-{i}", T0));

        _context.MlsJoinRequests.Add(request);
        await _context.SaveChangesAsync();
        return request.Id;
    }

    [Test]
    public async Task AnAutoAdmittedDevice_IsAnnouncedToItsOwnerAndToTheConversation()
    {
        await SeedConversation(AdminId, MemberId);
        await _service.EnableAsync(ConversationId, ConversationId, null, AdminId, EnableDto(epoch: 0), T0);
        var requestId = await SeedPendingJoinRequest(requiresManualApproval: false);

        await _service.PublishCommitAsync(ConversationId, ConversationId, null, AdminId,
            new PublishMlsCommitDto
            {
                Epoch = 1, Commit = MlsWire.Commit(), SenderDeviceId = "device-a",
                FulfilledJoinRequestIds = [requestId],
                Welcomes = [new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-new", Welcome = MlsWire.Welcome() }],
            }, [AdminId, MemberId], T0);

        // Nobody was asked.
        var owner = Sent.Sends.Single(s => s.Method == "identity.DeviceAdmitted");
        Assert.That(owner.Target, Is.EqualTo("user:" + MemberId));
        Assert.That(Sent.Sends.Any(s => s.Method == "conversation.MlsDeviceAdmitted"), Is.True);
    }

    [Test]
    public async Task AHumanApprovedDevice_IsNotReportedAsAutoAdmitted()
    {
        await SeedConversation(AdminId, MemberId);
        await _service.EnableAsync(ConversationId, ConversationId, null, AdminId, EnableDto(epoch: 0), T0);
        var requestId = await SeedPendingJoinRequest(requiresManualApproval: true, approvals: 1);

        await _service.PublishCommitAsync(ConversationId, ConversationId, null, AdminId,
            new PublishMlsCommitDto
            {
                Epoch = 1, Commit = MlsWire.Commit(), SenderDeviceId = "device-a",
                FulfilledJoinRequestIds = [requestId],
                Welcomes = [new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-new", Welcome = MlsWire.Welcome() }],
            }, [AdminId, MemberId], T0);

        // Somebody on the account already saw this one and allowed it; raising it as a security
        // event teaches people to dismiss the ones that matter.
        Assert.That(Sent.Sends.Any(s => s.Method == "identity.DeviceAdmitted"), Is.False);
        Assert.That(Sent.Sends.Any(s => s.Method == "conversation.MlsDeviceAdmitted"), Is.True);
    }

    [Test]
    public async Task PublishConversationCommit_NotifiesMembersDirectly()
    {
        await SeedConversation(AdminId, MemberId);
        await _service.EnableAsync(ConversationId, ConversationId, null, AdminId, EnableDto(epoch: 0), T0);

        await _service.PublishCommitAsync(ConversationId, ConversationId, null, AdminId,
            new PublishMlsCommitDto { Epoch = 1, Commit = MlsWire.Commit(), SenderDeviceId = "device-a" },
            [AdminId, MemberId], T0);

        // Conversation membership is known locally, so this one does not go via the bus.
        Assert.That(Sent.Sends.Any(s => s.Method == "conversation.MlsCommit"), Is.True);
        Assert.That(_bus.Published.OfType<ChannelMlsCommitPublished>(), Is.Empty);
    }
}
