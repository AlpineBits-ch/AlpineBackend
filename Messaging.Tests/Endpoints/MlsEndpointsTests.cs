using Echo.Realtime.Devices;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// The HTTP layer over <see cref="MlsGroupService"/>: who is allowed to do what, and the Welcome
/// fetch/ack pair.
/// </summary>
[TestFixture]
public class MlsEndpointsTests
{
    private const string ConversationId = "conv-1";
    private const string ChannelId = "chan-1";
    private const string OwnerId = "user-1";
    private const string PeerId = "user-2";

    private TestMessagingContext _context = null!;
    private ConversationPermissionService _permissions = null!;
    private FakeMessagingHubContext _hub = null!;
    private MlsGroupService _mls = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _permissions = new ConversationPermissionService(_context, new FakeDistributedCache());
        _hub = new FakeMessagingHubContext();
        _mls = new MlsGroupService(_context, _hub, new FakeMessageBus(), new MlsJoinRequestService(_context));
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Bus that answers every channel-permission question the same way.</summary>
    private static FakeMessageBus ChannelBus(bool allowed) => new(msg => msg switch
    {
        HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
        {
            ChannelId = r.ChannelId,
            UserId = r.UserId,
            IsAllowed = allowed,
            Permission = r.Permission,
        },
        _ => throw new InvalidOperationException("unexpected"),
    });

    /// <summary>Bus that allows only one specific permission, so tests can prove the gate is on the
    /// permission they expect rather than on "any permission at all".</summary>
    private static FakeMessageBus ChannelBusAllowing(ExternalPermission granted) => new(msg => msg switch
    {
        HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
        {
            ChannelId = r.ChannelId,
            UserId = r.UserId,
            IsAllowed = r.Permission == granted,
            Permission = r.Permission,
        },
        _ => throw new InvalidOperationException("unexpected"),
    });

    private static ConversationMember MakeMember(string id, string userId) => new()
    {
        Id = id,
        UserId = userId,
        ConversationId = ConversationId,
        PublicKey = [],
        CachedUserName = "test-user",
        CachedUserHash = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedEncryptedConversation(long epoch = 1)
    {
        _context.Conversations.Add(new Conversation
        {
            Id = ConversationId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            EncryptionState = ChannelEncryptionState.Encrypted,
            MlsGroupId = [1, 2, 3],
            MlsEpoch = epoch,
            MlsGroupInfo = [4, 5, 6],
            Members = [MakeMember("m-1", OwnerId), MakeMember("m-2", PeerId)],
        });
        _context.MlsGroupGenerations.Add(MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
        {
            ContextId = ConversationId,
            ConversationId = ConversationId,
            Generation = 1,
            MlsGroupId = [1, 2, 3],
            MlsGroupInfo = [4, 5, 6],
            Epoch = epoch,
            ActivatedByUserId = OwnerId,
        }));
        await _context.SaveChangesAsync();
    }

    private static PublishMlsCommitDto CommitDto(long epoch) => new()
    {
        Epoch = epoch,
        Commit = [10, 11, 12],
        SenderDeviceId = "device-a",
    };

    // ══════════════════════════════════════════════════════════════════════════ Conversation
    // authorization ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishCommit_Unauthenticated_ReturnsUnauthorized()
    {
        await SeedEncryptedConversation();

        var result = await MlsEndpoints.PublishCommit(
            ConversationId, CommitDto(2), TestPrincipal.Anonymous(), _context, _permissions, _mls);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task PublishCommit_NonMember_ReturnsForbid()
    {
        await SeedEncryptedConversation();

        var result = await MlsEndpoints.PublishCommit(
            ConversationId, CommitDto(2), TestPrincipal.ForUser("outsider"), _context, _permissions, _mls);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task PublishCommit_Member_Succeeds()
    {
        await SeedEncryptedConversation();

        var result = await MlsEndpoints.PublishCommit(
            ConversationId, CommitDto(2), TestPrincipal.ForUser(OwnerId), _context, _permissions, _mls);

        Assert.That(result, Is.InstanceOf<Ok<object>>().Or.InstanceOf<IValueHttpResult>());
        Assert.That(await _context.MlsCommits.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task PublishCommit_WrongEpoch_ReturnsConflict()
    {
        await SeedEncryptedConversation(epoch: 1);

        var result = await MlsEndpoints.PublishCommit(
            ConversationId, CommitDto(5), TestPrincipal.ForUser(OwnerId), _context, _permissions, _mls);

        Assert.That(result, Is.InstanceOf<Conflict<object>>().Or.InstanceOf<IValueHttpResult>());
        Assert.That(await _context.MlsCommits.AnyAsync(), Is.False);
    }

    [Test]
    public async Task GetCommits_NonMember_ReturnsForbid()
    {
        await SeedEncryptedConversation();

        var result = await MlsEndpoints.GetCommits(
            ConversationId, 0, null, TestPrincipal.ForUser("outsider"), _permissions, _mls);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetConversationMlsState_NonMember_ReturnsForbid()
    {
        await SeedEncryptedConversation();

        var result = await MlsEndpoints.GetConversationMlsState(
            ConversationId, TestPrincipal.ForUser("outsider"), _permissions, _mls);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetConversationMlsState_Member_ReportsTheActiveGeneration()
    {
        await SeedEncryptedConversation();

        var result = await MlsEndpoints.GetConversationMlsState(
            ConversationId, TestPrincipal.ForUser(OwnerId), _permissions, _mls);

        var state = ((Ok<MlsContextStateDto>)result).Value!;
        Assert.Multiple(() =>
        {
            Assert.That(state.Encrypted, Is.True);
            Assert.That(state.ActiveGeneration, Is.EqualTo(1));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Channel
    // authorization ══════════════════════════════════════════════════════════════════════════

    private static EnableMlsDto EnableDto() => new()
    {
        MlsGroupId = [1, 2, 3],
        MlsGroupInfo = [4, 5, 6],
        Epoch = 1,
    };

    [Test]
    public async Task EnableChannelMls_WithoutManageChannel_ReturnsForbid()
    {
        var result = await MlsEndpoints.EnableChannelMls(
            ChannelId, EnableDto(), TestPrincipal.ForUser(OwnerId), ChannelBus(allowed: false), _mls);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(await _context.MlsGroupGenerations.AnyAsync(), Is.False);
    }

    [Test]
    public async Task EnableChannelMls_WithOnlyViewChannel_ReturnsForbid()
    {
        // Turning a room end-to-end encrypted changes what everyone in it can read.
        var result = await MlsEndpoints.EnableChannelMls(
            ChannelId, EnableDto(), TestPrincipal.ForUser(OwnerId),
            ChannelBusAllowing(ExternalPermission.ViewChannel), _mls);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task EnableChannelMls_WithManageChannel_Succeeds()
    {
        var result = await MlsEndpoints.EnableChannelMls(
            ChannelId, EnableDto(), TestPrincipal.ForUser(OwnerId),
            ChannelBusAllowing(ExternalPermission.ManageChannel), _mls);

        Assert.That(result, Is.InstanceOf<Ok<object>>().Or.InstanceOf<IValueHttpResult>());
        var generation = await _context.MlsGroupGenerations.SingleAsync();
        Assert.That(generation.ChannelId, Is.EqualTo(ChannelId));
    }

    [Test]
    public async Task DisableChannelMls_WithoutManageChannel_ReturnsForbid()
    {
        var result = await MlsEndpoints.DisableChannelMls(
            ChannelId, TestPrincipal.ForUser(OwnerId),
            ChannelBusAllowing(ExternalPermission.ViewChannel), _mls);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetChannelMlsState_NeedsOnlyViewChannel()
    {
        // Anyone who can read the channel has to know whether to encrypt and under which generation.
        var result = await MlsEndpoints.GetChannelMlsState(
            ChannelId, TestPrincipal.ForUser(OwnerId),
            ChannelBusAllowing(ExternalPermission.ViewChannel), _mls);

        Assert.That(result, Is.InstanceOf<Ok<MlsContextStateDto>>());
    }

    [Test]
    public async Task GetChannelMlsState_WithoutViewChannel_ReturnsForbid()
    {
        var result = await MlsEndpoints.GetChannelMlsState(
            ChannelId, TestPrincipal.ForUser(OwnerId), ChannelBus(allowed: false), _mls);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task PublishChannelCommit_NeedsOnlyViewChannel()
    {
        // Every member has to be able to publish commits or the group cannot follow the roster as
        // people gain and lose access. Toggling encryption is the privileged action, not this.
        await MlsEndpoints.EnableChannelMls(
            ChannelId, new EnableMlsDto { MlsGroupId = [1], Epoch = 0 }, TestPrincipal.ForUser(OwnerId),
            ChannelBusAllowing(ExternalPermission.ManageChannel), _mls);

        var result = await MlsEndpoints.PublishChannelCommit(
            ChannelId, CommitDto(1), TestPrincipal.ForUser(PeerId),
            ChannelBusAllowing(ExternalPermission.ViewChannel), _mls);

        Assert.That(result, Is.InstanceOf<Ok<object>>().Or.InstanceOf<IValueHttpResult>());
        Assert.That(await _context.MlsCommits.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task PublishChannelCommit_WithoutViewChannel_ReturnsForbid()
    {
        var result = await MlsEndpoints.PublishChannelCommit(
            ChannelId, CommitDto(1), TestPrincipal.ForUser(PeerId), ChannelBus(allowed: false), _mls);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetChannelCommits_WithoutViewChannel_ReturnsForbid()
    {
        var result = await MlsEndpoints.GetChannelCommits(
            ChannelId, 0, null, TestPrincipal.ForUser(PeerId), ChannelBus(allowed: false), _mls);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Welcomes - new device-scoped contract
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<PendingWelcome> SeedWelcome(string userId, string deviceId, DateTimeOffset? consumedAt = null)
    {
        var welcome = PendingWelcome.Create(new CreatePendingWelcomeParams
        {
            ContextId = ConversationId,
            UserId = userId,
            DeviceId = deviceId,
            Welcome = [1, 2, 3],
            Generation = 1,
            Epoch = 1,
        });
        welcome.ConsumedAt = consumedAt;
        _context.PendingWelcomes.Add(welcome);
        await _context.SaveChangesAsync();
        return welcome;
    }

    private async Task<List<PendingWelcomeDto>> GetWelcomes(string? deviceId, string userId = PeerId)
    {
        var result = await MlsEndpoints.GetWelcomes(deviceId, TestPrincipal.ForUser(userId), _context);
        return ((Ok<IEnumerable<PendingWelcomeDto>>)result).Value!.ToList();
    }

    [Test]
    public async Task GetWelcomes_WithDeviceId_DoesNotConsumeOnRead()
    {
        await SeedWelcome(PeerId, "device-b");

        var first = await GetWelcomes("device-b");
        var second = await GetWelcomes("device-b");

        // Consuming on read throws away the only copy of a single-use init key whenever the join
        // fails, which locks that device out of the group permanently.
        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Has.Count.EqualTo(1), "A re-read before acknowledgement returns the same Welcome");
        });
    }

    [Test]
    public async Task GetWelcomes_WithDeviceId_OnlyReturnsThatDevices()
    {
        await SeedWelcome(PeerId, "device-b");
        await SeedWelcome(PeerId, "device-c");

        var welcomes = await GetWelcomes("device-b");

        // A user's other device holds a different leaf and a different init key - it can neither use
        // this Welcome nor be allowed to drain it.
        Assert.That(welcomes.Select(w => w.DeviceId), Is.EquivalentTo(new[] { "device-b" }));
    }

    [Test]
    public async Task GetWelcomes_OnlyReturnsWelcomesForTheRequestingUser()
    {
        await SeedWelcome(OwnerId, "device-b");

        Assert.That(await GetWelcomes("device-b"), Is.Empty);
    }

    [Test]
    public async Task GetWelcomes_ExcludesAlreadyAcknowledged()
    {
        await SeedWelcome(PeerId, "device-b", consumedAt: DateTimeOffset.UtcNow);

        Assert.That(await GetWelcomes("device-b"), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Welcomes - the legacy no-deviceId call keeps working, but stops destroying things
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetWelcomes_WithoutDeviceId_ReturnsTheUsersWelcomes()
    {
        // The shipped client sends no deviceId.
        await SeedWelcome(PeerId, "device-b");

        var welcomes = await GetWelcomes(null);

        Assert.That(welcomes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetWelcomes_WithoutDeviceId_NoLongerConsumesOnRead()
    {
        await SeedWelcome(PeerId, "device-b");

        await GetWelcomes(null);

        // This is the actual bug, and it was unrecoverable: the legacy branch stamped every Welcome
        // for every one of the user's devices consumed, so a laptop's poll destroyed the phone's
        // only way into the group.
        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.Null);
        Assert.That(await GetWelcomes(null), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetWelcomes_LegacyFetch_LeavesAnotherDevicesWelcomeClaimable()
    {
        // The exact cross-device loss the old branch caused: device-b polls without a deviceId, and
        // device-c - which holds the leaf that Welcome was sealed to - must still be able to fetch
        // and use it afterwards.
        await SeedWelcome(PeerId, "device-c");

        await GetWelcomes(null);

        Assert.That(await GetWelcomes("device-c"), Has.Count.EqualTo(1),
            "A sibling device's poll must not consume this device's only way into the group");
    }

    [Test]
    public async Task GetWelcomes_WithoutDeviceId_DoesNotTouchOtherUsers()
    {
        await SeedWelcome(PeerId, "device-b");
        await SeedWelcome(OwnerId, "device-z");

        await GetWelcomes(null);

        Assert.That(
            await _context.PendingWelcomes.SingleAsync(w => w.UserId == OwnerId),
            Has.Property("ConsumedAt").Null);
    }

    // ══════════════════════════════════════════════════════════════════════════ Ack - scoped by
    // (user, device) ══════════════════════════════════════════════════════════════════════════

    private static DefaultHttpContext HttpWithDevice(string? deviceId)
    {
        var http = new DefaultHttpContext();
        if (deviceId is not null) http.Request.Headers[DeviceIdentity.HeaderName] = deviceId;
        return http;
    }

    [Test]
    public async Task AckWelcomes_MarksThemConsumed()
    {
        var welcome = await SeedWelcome(PeerId, "device-b");

        var result = await MlsEndpoints.AckWelcomes(
            new AckWelcomesDto { WelcomeIds = [welcome.Id], DeviceId = "device-b" },
            TestPrincipal.ForUser(PeerId), HttpWithDevice(null), _context);

        Assert.That(((Ok<AckWelcomesResultDto>)result).Value!.Acknowledged, Is.EqualTo(1));
        Assert.That(await GetWelcomes("device-b"), Is.Empty);
    }

    [Test]
    public async Task AckWelcomes_CannotAcknowledgeAnotherDevicesWelcome()
    {
        // The same user, a different leaf. device-c cannot use this Welcome, and consuming it would
        // leave device-b permanently outside the group with nothing to say why.
        var welcome = await SeedWelcome(PeerId, "device-b");

        await MlsEndpoints.AckWelcomes(
            new AckWelcomesDto { WelcomeIds = [welcome.Id], DeviceId = "device-c" },
            TestPrincipal.ForUser(PeerId), HttpWithDevice(null), _context);

        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.Null);
    }

    [Test]
    public async Task AckWelcomes_CannotAcknowledgeAnotherUsersWelcome()
    {
        var welcome = await SeedWelcome(PeerId, "device-b");

        await MlsEndpoints.AckWelcomes(
            new AckWelcomesDto { WelcomeIds = [welcome.Id], DeviceId = "device-b" },
            TestPrincipal.ForUser("attacker"), HttpWithDevice(null), _context);

        // Otherwise anyone who learned an id could consume a Welcome on someone else's behalf and
        // strand that device outside the group.
        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.Null);
    }

    [Test]
    public async Task AckWelcomes_FallsBackToTheValidatedDeviceHeader()
    {
        // An old client sends no deviceId in the body.
        var welcome = await SeedWelcome(PeerId, "device-b");

        var result = await MlsEndpoints.AckWelcomes(
            new AckWelcomesDto { WelcomeIds = [welcome.Id] },
            TestPrincipal.ForUser(PeerId), HttpWithDevice("device-b"), _context);

        Assert.That(((Ok<AckWelcomesResultDto>)result).Value!.Acknowledged, Is.EqualTo(1));
    }

    [Test]
    public async Task AckWelcomes_WithNoDeviceAnywhere_IsRefusedRatherThanAckingBroadly()
    {
        var welcome = await SeedWelcome(PeerId, "device-b");

        var result = await MlsEndpoints.AckWelcomes(
            new AckWelcomesDto { WelcomeIds = [welcome.Id] },
            TestPrincipal.ForUser(PeerId), HttpWithDevice(null), _context);

        // The one thing that must not happen is acking across every device again.
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.Null);
    }

    [Test]
    public async Task AckWelcomes_IsIdempotent()
    {
        var welcome = await SeedWelcome(PeerId, "device-b");
        var dto = new AckWelcomesDto { WelcomeIds = [welcome.Id], DeviceId = "device-b" };

        await MlsEndpoints.AckWelcomes(dto, TestPrincipal.ForUser(PeerId), HttpWithDevice(null), _context);
        var consumedAt = (await _context.PendingWelcomes.SingleAsync()).ConsumedAt;

        await MlsEndpoints.AckWelcomes(dto, TestPrincipal.ForUser(PeerId), HttpWithDevice(null), _context);

        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.EqualTo(consumedAt),
            "A retried ack must not move the timestamp");
    }

    [Test]
    public async Task AckWelcomes_EmptyList_IsANoOp()
    {
        await SeedWelcome(PeerId, "device-b");

        var result = await MlsEndpoints.AckWelcomes(
            new AckWelcomesDto(), TestPrincipal.ForUser(PeerId), HttpWithDevice(null), _context);

        Assert.That(((Ok<AckWelcomesResultDto>)result).Value!.Acknowledged, Is.EqualTo(0));
        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.Null);
    }
}
