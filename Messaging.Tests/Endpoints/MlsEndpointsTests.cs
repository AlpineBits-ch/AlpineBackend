using Echo.Realtime;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Covers the MLS transport: commit publication (epoch ordering, fork refusal, Welcome carriage,
/// retention), catch-up reads, and the non-destructive device-scoped Welcome fetch/ack pair.
/// </summary>
[TestFixture]
public class MlsEndpointsTests
{
    private const string ConversationId = "conv-1";
    private const string OwnerId = "user-1";
    private const string PeerId = "user-2";

    private TestMessagingContext _context = null!;
    private ConversationPermissionService _permissions = null!;
    private FakeMessagingHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _permissions = new ConversationPermissionService(_context, new FakeDistributedCache());
        _hub = new FakeMessagingHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private FakeHubClients Sent => (FakeHubClients)_hub.Clients;

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

    private async Task<Conversation> SeedEncryptedConversation(long epoch = 1)
    {
        var conversation = new Conversation
        {
            Id = ConversationId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            EncryptionState = ChannelEncryptionState.Encrypted,
            MlsGroupId = [1, 2, 3],
            MlsEpoch = epoch,
            MlsGroupInfo = [4, 5, 6],
            Members = [MakeMember("m-1", OwnerId), MakeMember("m-2", PeerId)],
        };
        _context.Conversations.Add(conversation);
        await _context.SaveChangesAsync();
        return conversation;
    }

    private static PublishMlsCommitDto CommitDto(long epoch, params DeviceWelcomeDto[] welcomes) => new()
    {
        Epoch = epoch,
        Commit = [10, 11, 12],
        SenderDeviceId = "device-a",
        Welcomes = welcomes.ToList(),
    };

    private Task<IResult> Publish(PublishMlsCommitDto dto, string userId = OwnerId) =>
        MlsEndpoints.PublishCommit(ConversationId, dto, TestPrincipal.ForUser(userId), _context, _permissions, _hub);

    // ══════════════════════════════════════════════════════════════════════════
    // PublishCommit - authorization and shape
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishCommit_Unauthenticated_ReturnsUnauthorized()
    {
        await SeedEncryptedConversation();

        var result = await MlsEndpoints.PublishCommit(
            ConversationId, CommitDto(2), TestPrincipal.Anonymous(), _context, _permissions, _hub);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task PublishCommit_NonMember_ReturnsForbid()
    {
        await SeedEncryptedConversation();

        var result = await Publish(CommitDto(2), "outsider");

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task PublishCommit_EmptyCommitBytes_ReturnsBadRequest()
    {
        await SeedEncryptedConversation();

        var dto = CommitDto(2);
        dto.Commit = [];

        Assert.That(await Publish(dto), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task PublishCommit_MissingSenderDeviceId_ReturnsBadRequest()
    {
        await SeedEncryptedConversation();

        var dto = CommitDto(2);
        dto.SenderDeviceId = "";

        Assert.That(await Publish(dto), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task PublishCommit_PlainConversation_ReturnsBadRequest()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = ConversationId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            EncryptionState = ChannelEncryptionState.Plain,
            Members = [MakeMember("m-1", OwnerId)],
        });
        await _context.SaveChangesAsync();

        Assert.That(await Publish(CommitDto(1)), Is.InstanceOf<BadRequest<string>>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PublishCommit - epoch ordering, the part that silently forks clients
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishCommit_NextEpoch_StoresCommitAndAdvancesConversation()
    {
        await SeedEncryptedConversation(epoch: 1);

        var result = await Publish(CommitDto(2));

        var stored = await _context.MlsCommits.SingleAsync();
        var conversation = await _context.Conversations.SingleAsync();
        var ok = (Ok<MlsCommitPublishedDto>)result;
        Assert.Multiple(() =>
        {
            Assert.That(ok.Value!.Epoch, Is.EqualTo(2));
            Assert.That(ok.Value.ConversationId, Is.EqualTo(ConversationId));
            Assert.That(stored.Epoch, Is.EqualTo(2));
            Assert.That(stored.ContextId, Is.EqualTo(ConversationId));
            Assert.That(stored.ConversationId, Is.EqualTo(ConversationId));
            Assert.That(stored.SenderUserId, Is.EqualTo(OwnerId));
            Assert.That(stored.SenderDeviceId, Is.EqualTo("device-a"));
            Assert.That(conversation.MlsEpoch, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task PublishCommit_SkippingAnEpoch_IsRefused()
    {
        await SeedEncryptedConversation(epoch: 1);

        // A gap means some member's change never reached the server.
        var result = await Publish(CommitDto(3));

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.InstanceOf<Conflict<MlsEpochConflictDto>>());
            Assert.That(await _context.MlsCommits.AnyAsync(), Is.False);
            Assert.That((await _context.Conversations.SingleAsync()).MlsEpoch, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task PublishCommit_StaleEpoch_IsRefusedWithCurrentEpoch()
    {
        await SeedEncryptedConversation(epoch: 5);

        var result = await Publish(CommitDto(3));

        var conflict = (Conflict<MlsEpochConflictDto>)result;
        Assert.Multiple(() =>
        {
            Assert.That(conflict.Value!.CurrentEpoch, Is.EqualTo(5), "The loser needs to know where the group actually is");
            Assert.That(conflict.Value.RejectedEpoch, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task PublishCommit_EpochAlreadyTaken_IsRefused()
    {
        await SeedEncryptedConversation(epoch: 1);
        _context.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = ConversationId,
            ConversationId = ConversationId,
            Epoch = 2,
            Commit = [1],
            SenderUserId = PeerId,
            SenderDeviceId = "device-b",
        }));
        await _context.SaveChangesAsync();

        // Conversation is still on epoch 1 (the winner's write is not visible in MlsEpoch yet), so
        // the epoch arithmetic passes and only the row check catches the duplicate.
        var result = await Publish(CommitDto(2));

        Assert.That(result, Is.InstanceOf<Conflict<MlsEpochConflictDto>>());
        Assert.That(await _context.MlsCommits.CountAsync(), Is.EqualTo(1), "The second commit for an epoch must not be stored");
    }

    [Test]
    public async Task PublishCommit_SequentialCommits_AllStoredInOrder()
    {
        await SeedEncryptedConversation(epoch: 0);

        for (var epoch = 1; epoch <= 3; epoch++) await Publish(CommitDto(epoch));

        var epochs = await _context.MlsCommits.OrderBy(c => c.Epoch).Select(c => c.Epoch).ToListAsync();
        Assert.That(epochs, Is.EqualTo(new long[] { 1, 2, 3 }));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PublishCommit - Welcomes and GroupInfo
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishCommit_WithWelcomes_PersistsThemAgainstTheCommitEpoch()
    {
        await SeedEncryptedConversation(epoch: 1);

        await Publish(CommitDto(2, new DeviceWelcomeDto { UserId = PeerId, DeviceId = "device-b", Welcome = [7, 7] }));

        var welcome = await _context.PendingWelcomes.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(welcome.UserId, Is.EqualTo(PeerId));
            Assert.That(welcome.DeviceId, Is.EqualTo("device-b"));
            Assert.That(welcome.Epoch, Is.EqualTo(2), "The joiner lands on the epoch the commit created");
            Assert.That(welcome.ContextId, Is.EqualTo(ConversationId));
            Assert.That(welcome.ConsumedAt, Is.Null);
        });
    }

    [Test]
    public async Task PublishCommit_SkipsMalformedWelcomes()
    {
        await SeedEncryptedConversation(epoch: 1);

        await Publish(CommitDto(2,
            new DeviceWelcomeDto { UserId = PeerId, DeviceId = "", Welcome = [7] },
            new DeviceWelcomeDto { UserId = PeerId, DeviceId = "device-b", Welcome = [] },
            new DeviceWelcomeDto { UserId = PeerId, DeviceId = "device-c", Welcome = [7] }));

        var welcomes = await _context.PendingWelcomes.ToListAsync();
        Assert.That(welcomes.Select(w => w.DeviceId), Is.EquivalentTo(new[] { "device-c" }));
    }

    [Test]
    public async Task PublishCommit_RefreshesGroupInfoWhenSupplied()
    {
        await SeedEncryptedConversation(epoch: 1);

        var dto = CommitDto(2);
        dto.GroupInfo = [9, 9, 9];
        await Publish(dto);

        Assert.That((await _context.Conversations.SingleAsync()).MlsGroupInfo, Is.EqualTo(new byte[] { 9, 9, 9 }));
    }

    [Test]
    public async Task PublishCommit_KeepsExistingGroupInfoWhenOmitted()
    {
        await SeedEncryptedConversation(epoch: 1);

        await Publish(CommitDto(2));

        Assert.That((await _context.Conversations.SingleAsync()).MlsGroupInfo, Is.EqualTo(new byte[] { 4, 5, 6 }));
    }

    // ══════════════════════════════════════════════════════════════════════════ PublishCommit -
    // fanout targeting ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishCommit_NotifiesEveryMemberOfTheNewEpoch()
    {
        await SeedEncryptedConversation(epoch: 1);

        await Publish(CommitDto(2));

        var nudge = Sent.Sends.Single(s => s.Method == "conversation.MlsCommit");
        Assert.That(nudge.Target, Does.Contain(OwnerId).And.Contain(PeerId));
    }

    [Test]
    public async Task PublishCommit_PushesWelcomeToTheOwningDeviceOnly()
    {
        await SeedEncryptedConversation(epoch: 1);

        await Publish(CommitDto(2, new DeviceWelcomeDto { UserId = PeerId, DeviceId = "device-b", Welcome = [7] }));

        // A Welcome is sealed to one leaf.
        var push = Sent.Sends.Single(s => s.Method == "conversation.Welcome");
        Assert.That(push.Target, Is.EqualTo("group:" + EchoRealtimeHub.DeviceGroup(PeerId, "device-b")));
    }

    // ══════════════════════════════════════════════════════════════════════════ PublishCommit -
    // retention ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishCommit_SweepsCommitsPastTheRetentionWindow()
    {
        await SeedEncryptedConversation(epoch: 1);

        var ancient = MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = ConversationId,
            ConversationId = ConversationId,
            Epoch = 1,
            Commit = [1],
            SenderUserId = OwnerId,
            SenderDeviceId = "device-a",
        });
        ancient.CreatedAt = DateTimeOffset.UtcNow - MlsEndpoints.CommitRetention - TimeSpan.FromDays(1);
        _context.MlsCommits.Add(ancient);
        await _context.SaveChangesAsync();

        await Publish(CommitDto(2));

        var remaining = await _context.MlsCommits.Select(c => c.Epoch).ToListAsync();
        Assert.That(remaining, Is.EqualTo(new long[] { 2 }));
    }

    // ══════════════════════════════════════════════════════════════════════════ GetCommits
    // ══════════════════════════════════════════════════════════════════════════

    private async Task SeedCommits(params long[] epochs)
    {
        foreach (var epoch in epochs)
        {
            _context.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
            {
                ContextId = ConversationId,
                ConversationId = ConversationId,
                Epoch = epoch,
                Commit = [(byte)epoch],
                SenderUserId = OwnerId,
                SenderDeviceId = "device-a",
            }));
        }
        await _context.SaveChangesAsync();
    }

    private async Task<List<MlsCommitResponseDto>> GetCommits(long sinceEpoch, string userId = OwnerId)
    {
        var result = await MlsEndpoints.GetCommits(
            ConversationId, sinceEpoch, TestPrincipal.ForUser(userId), _context, _permissions);
        var ok = (Ok<IEnumerable<MlsCommitResponseDto>>)result;
        return ok.Value!.ToList();
    }

    [Test]
    public async Task GetCommits_NonMember_ReturnsForbid()
    {
        await SeedEncryptedConversation();

        var result = await MlsEndpoints.GetCommits(
            ConversationId, 0, TestPrincipal.ForUser("outsider"), _context, _permissions);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetCommits_ReturnsOnlyCommitsAfterTheGivenEpoch()
    {
        await SeedEncryptedConversation();
        await SeedCommits(1, 2, 3, 4);

        var commits = await GetCommits(2);

        Assert.That(commits.Select(c => c.Epoch), Is.EqualTo(new long[] { 3, 4 }));
    }

    [Test]
    public async Task GetCommits_ReturnsThemInEpochOrder()
    {
        await SeedEncryptedConversation();
        await SeedCommits(3, 1, 4, 2);

        var commits = await GetCommits(0);

        // Applying commits out of order forks the client permanently, so ordering is the whole
        // contract of this endpoint - not a presentational nicety.
        Assert.That(commits.Select(c => c.Epoch), Is.EqualTo(new long[] { 1, 2, 3, 4 }));
    }

    [Test]
    public async Task GetCommits_DoesNotLeakOtherGroupsCommits()
    {
        await SeedEncryptedConversation();
        await SeedCommits(1);
        _context.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = "conv-other",
            Epoch = 1,
            Commit = [99],
            SenderUserId = "someone",
            SenderDeviceId = "device-z",
        }));
        await _context.SaveChangesAsync();

        var commits = await GetCommits(0);

        Assert.That(commits, Has.Count.EqualTo(1));
        Assert.That(commits[0].ContextId, Is.EqualTo(ConversationId));
    }

    [Test]
    public async Task GetCommits_CapsThePage()
    {
        await SeedEncryptedConversation();
        await SeedCommits(Enumerable.Range(1, MlsEndpoints.MaxCommitPageSize + 50).Select(i => (long)i).ToArray());

        var commits = await GetCommits(0);

        Assert.That(commits, Has.Count.EqualTo(MlsEndpoints.MaxCommitPageSize));
        Assert.That(commits[0].Epoch, Is.EqualTo(1), "Paging must start at the oldest unapplied commit");
    }

    // ══════════════════════════════════════════════════════════════════════════ GetWelcomes /
    // AckWelcomes ══════════════════════════════════════════════════════════════════════════

    private async Task<PendingWelcome> SeedWelcome(string userId, string deviceId, DateTimeOffset? consumedAt = null)
    {
        var welcome = PendingWelcome.Create(new CreatePendingWelcomeParams
        {
            ContextId = ConversationId,
            ConversationId = null,
            UserId = userId,
            DeviceId = deviceId,
            Welcome = [1, 2, 3],
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
        var ok = (Ok<IEnumerable<PendingWelcomeDto>>)result;
        return ok.Value!.ToList();
    }

    [Test]
    public async Task GetWelcomes_WithoutDeviceId_ReturnsBadRequest()
    {
        var result = await MlsEndpoints.GetWelcomes(null, TestPrincipal.ForUser(PeerId), _context);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task GetWelcomes_DoesNotConsumeOnRead()
    {
        await SeedWelcome(PeerId, "device-b");

        var first = await GetWelcomes("device-b");
        var second = await GetWelcomes("device-b");

        // The old behaviour deleted on read.
        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Has.Count.EqualTo(1), "A re-read before acknowledgement must return the same Welcome");
        });
    }

    [Test]
    public async Task GetWelcomes_OnlyReturnsWelcomesForTheRequestingDevice()
    {
        await SeedWelcome(PeerId, "device-b");
        await SeedWelcome(PeerId, "device-c");

        var welcomes = await GetWelcomes("device-b");

        // A user's other device holds a different leaf and a different init key - it can neither
        // use this Welcome nor be allowed to drain it.
        Assert.That(welcomes.Select(w => w.DeviceId), Is.EquivalentTo(new[] { "device-b" }));
    }

    [Test]
    public async Task GetWelcomes_OnlyReturnsWelcomesForTheRequestingUser()
    {
        await SeedWelcome(OwnerId, "device-b");

        var welcomes = await GetWelcomes("device-b");

        Assert.That(welcomes, Is.Empty);
    }

    [Test]
    public async Task GetWelcomes_ExcludesAlreadyAcknowledged()
    {
        await SeedWelcome(PeerId, "device-b", consumedAt: DateTimeOffset.UtcNow);

        Assert.That(await GetWelcomes("device-b"), Is.Empty);
    }

    [Test]
    public async Task AckWelcomes_MarksThemConsumed()
    {
        var welcome = await SeedWelcome(PeerId, "device-b");

        var result = await MlsEndpoints.AckWelcomes(
            new AckWelcomesDto { WelcomeIds = [welcome.Id] }, TestPrincipal.ForUser(PeerId), _context);

        Assert.That(((Ok<AckWelcomesResultDto>)result).Value!.Acknowledged, Is.EqualTo(1));
        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.Not.Null);
        Assert.That(await GetWelcomes("device-b"), Is.Empty);
    }

    [Test]
    public async Task AckWelcomes_CannotAcknowledgeAnotherUsersWelcome()
    {
        var welcome = await SeedWelcome(PeerId, "device-b");

        await MlsEndpoints.AckWelcomes(
            new AckWelcomesDto { WelcomeIds = [welcome.Id] }, TestPrincipal.ForUser("attacker"), _context);

        // Otherwise anyone who learned an id could consume a Welcome on someone else's behalf and
        // strand that device outside the group.
        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.Null);
    }

    [Test]
    public async Task AckWelcomes_IsIdempotent()
    {
        var welcome = await SeedWelcome(PeerId, "device-b");
        var dto = new AckWelcomesDto { WelcomeIds = [welcome.Id] };

        await MlsEndpoints.AckWelcomes(dto, TestPrincipal.ForUser(PeerId), _context);
        var consumedAt = (await _context.PendingWelcomes.SingleAsync()).ConsumedAt;

        await MlsEndpoints.AckWelcomes(dto, TestPrincipal.ForUser(PeerId), _context);

        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.EqualTo(consumedAt),
            "A retried ack must not move the timestamp");
    }

    [Test]
    public async Task AckWelcomes_EmptyList_IsANoOp()
    {
        await SeedWelcome(PeerId, "device-b");

        var result = await MlsEndpoints.AckWelcomes(
            new AckWelcomesDto(), TestPrincipal.ForUser(PeerId), _context);

        Assert.That(((Ok<AckWelcomesResultDto>)result).Value!.Acknowledged, Is.EqualTo(0));
        Assert.That((await _context.PendingWelcomes.SingleAsync()).ConsumedAt, Is.Null);
    }
}
