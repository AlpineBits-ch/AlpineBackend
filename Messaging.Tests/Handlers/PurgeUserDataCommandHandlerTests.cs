using Identity.Contracts.Bus.Commands;
using Messaging.Application.Handler.Account;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Covers Messaging's participant in the AccountDeletionSaga fan-out: removes the deleted user's
/// ConversationMember rows (and their devices) but deliberately leaves Message/Reaction rows alone
/// (see the handler's class-level comment - those resolve live to the tombstoned user, same as
/// Discord's "Deleted User").
/// </summary>
[TestFixture]
public class PurgeUserDataCommandHandlerTests
{
    private TestMessagingContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestMessagingContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ConversationMember MakeMember(string id, string userId, string conversationId) => new()
    {
        Id = id,
        UserId = userId,
        ConversationId = conversationId,
        PublicKey = [],
        CachedUserName = "test-user",
        CachedUserHash = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task Handle_RemovesAllMembershipsForUser_AcrossConversations()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "user-a", "conv-1"),
            MakeMember("m-2", "user-a", "conv-2"),
            MakeMember("m-3", "user-b", "conv-1"));
        await _context.SaveChangesAsync();

        var response = await PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = "user-a" }, _context);
        await _context.SaveChangesAsync();

        var remaining = _context.Members.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(remaining, Has.Count.EqualTo(1));
            Assert.That(remaining[0].UserId, Is.EqualTo("user-b"));
            Assert.That(response.UserId, Is.EqualTo("user-a"));
            Assert.That(response.Service, Is.EqualTo("messaging"));
        });
    }

    [Test]
    public async Task Handle_RemovesAssociatedDevices()
    {
        var member = MakeMember("m-1", "user-a", "conv-1");
        member.Devices.Add(new ConversationMemberDevice
        {
            Id = "cmde-1",
            ConversationMemberId = "m-1",
            DeviceId = "device-1",
            MlsLeafIndex = 0,
        });
        _context.Members.Add(member);
        await _context.SaveChangesAsync();

        await PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = "user-a" }, _context);
        await _context.SaveChangesAsync();

        Assert.That(_context.MemberDevices.Any(), Is.False);
    }

    [Test]
    public async Task Handle_UserHasNoMemberships_ReturnsResponseWithoutError()
    {
        var response = await PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = "ghost" }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.UserId, Is.EqualTo("ghost"));
            Assert.That(response.Service, Is.EqualTo("messaging"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ MLS artifacts

    private void SeedJoinRequest(string userId, string contextId)
    {
        _context.MlsJoinRequests.Add(MlsJoinRequest.Create(new CreateMlsJoinRequestParams
        {
            ContextId = contextId,
            ConversationId = contextId,
            Generation = 1,
            RequesterUserId = userId,
            RequesterDeviceId = "device-" + userId,
            KeyPackage = [1, 2, 3],
            KeyPackageHash = "hash",
            SignatureKeyFingerprint = "AAAA-BBBB",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        }));
    }

    private void SeedWelcome(string userId, string contextId)
    {
        _context.PendingWelcomes.Add(PendingWelcome.Create(new CreatePendingWelcomeParams
        {
            ContextId = contextId,
            ConversationId = contextId,
            UserId = userId,
            DeviceId = "device-" + userId,
            Welcome = [9],
            Generation = 1,
            Epoch = 1,
        }));
    }

    [Test]
    public async Task Handle_RemovesTheUsersWelcomesAndJoinRequests()
    {
        _context.Members.Add(MakeMember("m-1", "user-a", "conv-1"));
        _context.Members.Add(MakeMember("m-2", "user-b", "conv-1"));
        SeedWelcome("user-a", "conv-1");
        SeedWelcome("user-b", "conv-1");
        SeedJoinRequest("user-a", "conv-1");
        SeedJoinRequest("user-b", "conv-1");
        await _context.SaveChangesAsync();

        await PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = "user-a" }, _context);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.PendingWelcomes.Select(w => w.UserId), Is.EqualTo(new[] { "user-b" }));
            Assert.That(_context.MlsJoinRequests.Select(r => r.RequesterUserId), Is.EqualTo(new[] { "user-b" }));
        });
    }

    [Test]
    public async Task Handle_KeepsCommitsAndGenerationsOfAConversationOthersAreStillIn()
    {
        _context.Members.Add(MakeMember("m-1", "user-a", "conv-1"));
        _context.Members.Add(MakeMember("m-2", "user-b", "conv-1"));
        _context.MlsGroupGenerations.Add(MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
        {
            ContextId = "conv-1", ConversationId = "conv-1", Generation = 1, MlsGroupId = [1],
            Epoch = 1, ActivatedByUserId = "user-a", ActivatedAt = DateTimeOffset.UtcNow,
        }));
        _context.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = "conv-1", ConversationId = "conv-1", Generation = 1, Epoch = 1,
            Commit = [1], SenderUserId = "user-a", SenderDeviceId = "device-user-a",
        }));
        await _context.SaveChangesAsync();

        await PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = "user-a" }, _context);
        await _context.SaveChangesAsync();

        // A commit is not the sender's property; it is a link in the group's history.
        Assert.Multiple(() =>
        {
            Assert.That(_context.MlsCommits.Count(), Is.EqualTo(1));
            Assert.That(_context.MlsGroupGenerations.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Handle_RemovesTheGroupOfAConversationNobodyIsLeftIn()
    {
        _context.Members.Add(MakeMember("m-1", "user-a", "conv-solo"));
        _context.MlsGroupGenerations.Add(MlsGroupGeneration.Create(new CreateMlsGroupGenerationParams
        {
            ContextId = "conv-solo", ConversationId = "conv-solo", Generation = 1, MlsGroupId = [1],
            Epoch = 1, ActivatedByUserId = "user-a", ActivatedAt = DateTimeOffset.UtcNow,
        }));
        _context.MlsCommits.Add(MlsCommit.Create(new CreateMlsCommitParams
        {
            ContextId = "conv-solo", ConversationId = "conv-solo", Generation = 1, Epoch = 1,
            Commit = [1], SenderUserId = "user-a", SenderDeviceId = "device-user-a",
        }));
        await _context.SaveChangesAsync();

        await PurgeUserDataCommandHandler.Handle(new PurgeUserDataCommand { UserId = "user-a" }, _context);
        await _context.SaveChangesAsync();

        // Nobody is left to be forked off, and nothing left to read.
        Assert.Multiple(() =>
        {
            Assert.That(_context.MlsCommits.Any(), Is.False);
            Assert.That(_context.MlsGroupGenerations.Any(), Is.False);
        });
    }
}
