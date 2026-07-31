using Echo.Realtime;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Events;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Tests.Services;

/// <summary>
/// The MLS group lifecycle: turning encryption on, turning it off, and doing both repeatedly.
/// </summary>
[TestFixture]
public class MlsGroupServiceTests
{
    private const string ChannelId = "chan-1";
    private const string ConversationId = "conv-1";
    private const string AdminId = "user-admin";
    private const string MemberId = "user-member";

    private TestMessagingContext _context = null!;
    private FakeMessagingHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private MlsGroupService _service = null!;

    /// <summary>Fixed clock.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset AfterCooldown(int toggles = 1) =>
        T0 + (MlsGroupService.ToggleCooldown + TimeSpan.FromSeconds(1)) * toggles;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _hub = new FakeMessagingHubContext();
        _bus = new FakeMessageBus();
        _service = new MlsGroupService(_context, _hub, _bus);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private FakeHubClients Sent => (FakeHubClients)_hub.Clients;

    private static EnableMlsDto EnableDto(long epoch = 1, params DeviceWelcomeDto[] welcomes) => new()
    {
        MlsGroupId = [1, 2, 3],
        MlsGroupInfo = [4, 5, 6],
        Epoch = epoch,
        Welcomes = welcomes.ToList(),
    };

    private Task<MlsOperationResult> EnableChannel(EnableMlsDto? dto = null, DateTimeOffset? at = null) =>
        _service.EnableAsync(ChannelId, null, ChannelId, AdminId, dto ?? EnableDto(), at ?? T0);

    private Task<MlsOperationResult> DisableChannel(DateTimeOffset? at = null) =>
        _service.DisableAsync(ChannelId, null, ChannelId, AdminId, at ?? AfterCooldown());

    // ══════════════════════════════════════════════════════════════════════════ Enabling
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Enable_OnAFreshContext_CreatesGenerationOne()
    {
        var result = await EnableChannel();

        var generation = await _context.MlsGroupGenerations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
            Assert.That(generation.Generation, Is.EqualTo(1));
            Assert.That(generation.State, Is.EqualTo(MlsGenerationState.Active));
            Assert.That(generation.ContextId, Is.EqualTo(ChannelId));
            Assert.That(generation.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(generation.ConversationId, Is.Null);
            Assert.That(generation.ActivatedByUserId, Is.EqualTo(AdminId));
        });
    }

    [Test]
    public async Task Enable_AtEpochZero_IsAccepted()
    {
        // A group whose creator had nobody to add sits at epoch 0 until the first commit.
        var result = await EnableChannel(EnableDto(epoch: 0));

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
        Assert.That((await _context.MlsGroupGenerations.SingleAsync()).Epoch, Is.EqualTo(0));
    }

    [Test]
    public async Task Enable_WithoutGroupId_IsRejected()
    {
        var dto = EnableDto();
        dto.MlsGroupId = [];

        Assert.That((await EnableChannel(dto)).Status, Is.EqualTo(MlsOperationStatus.BadRequest));
    }

    [Test]
    public async Task Enable_WhenAlreadyEncrypted_IsRefused()
    {
        await EnableChannel();

        var result = await EnableChannel(at: AfterCooldown());

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
        Assert.That(await _context.MlsGroupGenerations.CountAsync(), Is.EqualTo(1),
            "A second live group for one context would split the room in half");
    }

    [Test]
    public async Task Enable_StoresWelcomesAgainstTheNewGeneration()
    {
        await EnableChannel(EnableDto(1, new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-b", Welcome = [7] }));

        var welcome = await _context.PendingWelcomes.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(welcome.Generation, Is.EqualTo(1));
            Assert.That(welcome.ContextId, Is.EqualTo(ChannelId));
            Assert.That(welcome.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(welcome.DeviceId, Is.EqualTo("device-b"));
        });
    }

    [Test]
    public async Task Enable_PushesWelcomeToTheOwningDeviceOnly()
    {
        await EnableChannel(EnableDto(1, new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-b", Welcome = [7] }));

        var push = Sent.Sends.Single(s => s.Method == "conversation.Welcome");
        Assert.That(push.Target, Is.EqualTo("group:" + EchoRealtimeHub.DeviceGroup(MemberId, "device-b")));
    }

    [Test]
    public async Task Enable_OnAChannel_AnnouncesViaTheBus()
    {
        await EnableChannel();

        // Messaging does not know who is in a channel; Guild does.
        var announced = _bus.Published.OfType<ChannelMlsStateChanged>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(announced.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(announced.Encrypted, Is.True);
            Assert.That(announced.Generation, Is.EqualTo(1));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Disabling
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Disable_TerminatesTheActiveGenerationWithoutDeletingIt()
    {
        await EnableChannel();

        var result = await DisableChannel();

        var generation = await _context.MlsGroupGenerations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
            Assert.That(generation.State, Is.EqualTo(MlsGenerationState.Terminated));
            Assert.That(generation.TerminatedAt, Is.Not.Null);
            Assert.That(generation.TerminatedByUserId, Is.EqualTo(AdminId));
            // The row has to survive: messages sent under it are still ciphertext, and this is the
            // only record of which group can read them.
            Assert.That(generation.MlsGroupId, Is.Not.Null);
        });
    }

    [Test]
    public async Task Disable_ReportsWhichGenerationStoppedBeingReadable()
    {
        await EnableChannel();

        var result = await DisableChannel();

        var payload = (MlsToggleResultDto)result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(payload.Encrypted, Is.False);
            Assert.That(payload.TerminatedGeneration, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Disable_WhenAlreadyPlain_IsIdempotent()
    {
        // Two admins hitting the switch, or one client retrying a request whose response it never
        // saw, should converge on "it is off" rather than on an error nobody can act on.
        var result = await DisableChannel(at: T0);

        var payload = (MlsToggleResultDto)result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
            Assert.That(payload.AlreadyInRequestedState, Is.True);
            Assert.That(payload.Encrypted, Is.False);
        });
    }

    [Test]
    public async Task Disable_ConsumesUnusedWelcomesForTheDeadGeneration()
    {
        await EnableChannel(EnableDto(1, new DeviceWelcomeDto { UserId = MemberId, DeviceId = "device-b", Welcome = [7] }));

        await DisableChannel();

        // Otherwise a device offline for the whole encrypted stretch wakes up, joins a group that no
        // longer exists, and reports itself encrypted in a context that is not.
        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.Not.Null);
    }

    [Test]
    public async Task Disable_LeavesMessagesUntouched()
    {
        await EnableChannel();
        await DisableChannel();

        // Nothing decrypts or rewrites message rows - this is a state change, not a conversion.
        Assert.That(await _context.Messages.AnyAsync(), Is.False);
        Assert.That(await _context.MlsGroupGenerations.CountAsync(), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════ Toggling back and
    // forth ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ReEnabling_MintsANewGenerationRatherThanResumingTheOld()
    {
        await EnableChannel();
        await DisableChannel();

        await EnableChannel(at: AfterCooldown(2));

        var generations = await _context.MlsGroupGenerations.OrderBy(g => g.Generation).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(generations.Select(g => g.Generation), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(generations[0].State, Is.EqualTo(MlsGenerationState.Terminated));
            Assert.That(generations[1].State, Is.EqualTo(MlsGenerationState.Active));
        });
    }

    [Test]
    public async Task TogglingRepeatedly_KeepsEveryGenerationAndOnlyOneActive()
    {
        var clock = T0;
        for (var i = 0; i < 4; i++)
        {
            await EnableChannel(at: clock);
            clock += MlsGroupService.ToggleCooldown + TimeSpan.FromSeconds(1);
            await DisableChannel(at: clock);
            clock += MlsGroupService.ToggleCooldown + TimeSpan.FromSeconds(1);
        }
        await EnableChannel(at: clock);

        var generations = await _context.MlsGroupGenerations.OrderBy(g => g.Generation).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(generations, Has.Count.EqualTo(5), "Every generation is kept - its messages still exist");
            Assert.That(generations.Select(g => g.Generation), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
            Assert.That(generations.Count(g => g.State == MlsGenerationState.Active), Is.EqualTo(1));
            Assert.That(generations.Last().State, Is.EqualTo(MlsGenerationState.Active));
        });
    }

    [Test]
    public async Task CommitsFromDifferentGenerations_CoexistAtTheSameEpoch()
    {
        await EnableChannel(EnableDto(epoch: 0));
        await PublishChannelCommit(epoch: 1, at: T0);
        await DisableChannel(at: AfterCooldown());
        await EnableChannel(EnableDto(epoch: 0), at: AfterCooldown(2));
        await PublishChannelCommit(epoch: 1, at: AfterCooldown(2));

        // Both groups have an epoch 1.
        var commits = await _context.MlsCommits.OrderBy(c => c.Generation).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(commits, Has.Count.EqualTo(2));
            Assert.That(commits.Select(c => c.Generation), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(commits.Select(c => c.Epoch), Is.EqualTo(new long[] { 1, 1 }));
        });
    }

    [Test]
    public async Task GetCommits_ForOneGeneration_ExcludesTheOther()
    {
        await EnableChannel(EnableDto(epoch: 0));
        await PublishChannelCommit(epoch: 1, at: T0);
        await DisableChannel(at: AfterCooldown());
        await EnableChannel(EnableDto(epoch: 0), at: AfterCooldown(2));
        await PublishChannelCommit(epoch: 1, at: AfterCooldown(2));

        var firstGeneration = await _service.GetCommitsAsync(ChannelId, generation: 1, sinceEpoch: 0);
        var live = await _service.GetCommitsAsync(ChannelId, generation: null, sinceEpoch: 0);

        Assert.Multiple(() =>
        {
            Assert.That(firstGeneration.Select(c => c.Generation), Is.EqualTo(new[] { 1 }));
            Assert.That(live.Select(c => c.Generation), Is.EqualTo(new[] { 2 }),
                "No generation named means the live one");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Toggle cooldown
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Disable_ImmediatelyAfterEnable_IsRefusedWithARetryHint()
    {
        await EnableChannel(at: T0);

        var result = await _service.DisableAsync(ChannelId, null, ChannelId, AdminId, T0 + TimeSpan.FromSeconds(2));

        var conflict = (MlsToggleConflictDto)result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
            Assert.That(conflict.RetryAfterSeconds, Is.GreaterThan(0));
            Assert.That(conflict.Encrypted, Is.True, "The caller needs to know where it actually stands");
        });
    }

    [Test]
    public async Task Toggle_AfterTheCooldown_IsAllowed()
    {
        await EnableChannel(at: T0);

        var result = await DisableChannel(at: AfterCooldown());

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
    }

    [Test]
    public async Task Cooldown_DoesNotBlockTheFirstEnable()
    {
        Assert.That((await EnableChannel(at: T0)).Status, Is.EqualTo(MlsOperationStatus.Ok));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Commit publication against generations
    // ══════════════════════════════════════════════════════════════════════════

    private Task<MlsOperationResult> PublishChannelCommit(long epoch, int? generation = null, DateTimeOffset? at = null) =>
        _service.PublishCommitAsync(ChannelId, null, ChannelId, AdminId, new PublishMlsCommitDto
        {
            Epoch = epoch,
            Generation = generation,
            Commit = [10, 11],
            SenderDeviceId = "device-a",
        }, [], at ?? T0);

    [Test]
    public async Task PublishCommit_WhenEncryptionIsOff_IsRefused()
    {
        var result = await PublishChannelCommit(epoch: 1);

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
        Assert.That(((MlsToggleConflictDto)result.Value!).Encrypted, Is.False);
    }

    [Test]
    public async Task PublishCommit_AdvancesTheGenerationsEpoch()
    {
        await EnableChannel(EnableDto(epoch: 0));

        await PublishChannelCommit(epoch: 1);

        Assert.That((await _context.MlsGroupGenerations.SingleAsync()).Epoch, Is.EqualTo(1));
    }

    [Test]
    public async Task PublishCommit_SkippingAnEpoch_IsRefused()
    {
        await EnableChannel(EnableDto(epoch: 0));

        var result = await PublishChannelCommit(epoch: 3);

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
        Assert.That(await _context.MlsCommits.AnyAsync(), Is.False);
    }

    [Test]
    public async Task PublishCommit_ForAReplacedGeneration_IsRefused()
    {
        await EnableChannel(EnableDto(epoch: 0));
        await DisableChannel(at: AfterCooldown());
        await EnableChannel(EnableDto(epoch: 0), at: AfterCooldown(2));

        var result = await PublishChannelCommit(epoch: 1, generation: 1, at: AfterCooldown(2));

        // Applying a commit built for the old group would advance the wrong group and fork everyone
        // still on the live one.
        var conflict = (MlsEpochConflictDto)result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Conflict));
            Assert.That(conflict.CurrentGeneration, Is.EqualTo(2));
            Assert.That(conflict.RejectedGeneration, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task PublishCommit_WithoutAGeneration_TargetsTheLiveOne()
    {
        await EnableChannel(EnableDto(epoch: 0));

        // A client that predates generations sends none, and the only group it could have built
        // against is the live one.
        var result = await PublishChannelCommit(epoch: 1, generation: null);

        Assert.That(result.Status, Is.EqualTo(MlsOperationStatus.Ok));
        Assert.That((await _context.MlsCommits.SingleAsync()).Generation, Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Conversations keep their aggregate state in sync
    // ══════════════════════════════════════════════════════════════════════════

    private async Task SeedConversation()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = ConversationId,
            CreatedAt = T0,
            UpdatedAt = T0,
            EncryptionState = ChannelEncryptionState.Plain,
            Members =
            [
                new ConversationMember
                {
                    Id = "m-1", UserId = AdminId, ConversationId = ConversationId, PublicKey = [],
                    CachedUserName = "a", CachedUserHash = 0, CreatedAt = T0, UpdatedAt = T0,
                },
            ],
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Enable_OnAConversation_MarksTheAggregateEncrypted()
    {
        await SeedConversation();

        await _service.EnableAsync(ConversationId, ConversationId, null, AdminId, EnableDto(), T0);

        // Existing clients read encryption state off the conversation DTO, so the aggregate is kept
        // in sync rather than migrated away from.
        var conversation = await _context.Conversations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(conversation.EncryptionState, Is.EqualTo(ChannelEncryptionState.Encrypted));
            Assert.That(conversation.MlsGroupId, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(conversation.MlsEpoch, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Disable_OnAConversation_MarksTheAggregatePlainButKeepsGroupId()
    {
        await SeedConversation();
        await _service.EnableAsync(ConversationId, ConversationId, null, AdminId, EnableDto(), T0);

        await _service.DisableAsync(ConversationId, ConversationId, null, AdminId, AfterCooldown());

        var conversation = await _context.Conversations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(conversation.EncryptionState, Is.EqualTo(ChannelEncryptionState.Plain));
            // Clearing the group id would strand the already-sent ciphertext with nothing pointing
            // at the group that can read it.
            Assert.That(conversation.MlsGroupId, Is.EqualTo(new byte[] { 1, 2, 3 }));
        });
    }

    [Test]
    public async Task Enable_OnAConversation_NotifiesMembersDirectly()
    {
        await SeedConversation();

        await _service.EnableAsync(ConversationId, ConversationId, null, AdminId, EnableDto(), T0);

        // Conversation membership is known locally, so this one does not need Guild.
        Assert.That(Sent.Sends.Any(s => s.Method == "conversation.MlsStateChanged"), Is.True);
        Assert.That(_bus.Published.OfType<ChannelMlsStateChanged>(), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ State reporting
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetState_OnAPlainContext_ReportsUnencrypted()
    {
        var state = await _service.GetStateAsync(ChannelId);

        Assert.Multiple(() =>
        {
            Assert.That(state.Encrypted, Is.False);
            Assert.That(state.ActiveGeneration, Is.Null);
            Assert.That(state.Generations, Is.Empty);
        });
    }

    [Test]
    public async Task GetState_ListsTerminatedGenerationsToo()
    {
        await EnableChannel(at: T0);
        await DisableChannel(at: AfterCooldown());
        await EnableChannel(at: AfterCooldown(2));

        var state = await _service.GetStateAsync(ChannelId);

        // A client needs to know the old generation existed to explain the stretch of history it
        // cannot read, rather than rendering it as corrupt.
        Assert.Multiple(() =>
        {
            Assert.That(state.Encrypted, Is.True);
            Assert.That(state.ActiveGeneration, Is.EqualTo(2));
            Assert.That(state.Generations, Has.Count.EqualTo(2));
        });
    }
}
