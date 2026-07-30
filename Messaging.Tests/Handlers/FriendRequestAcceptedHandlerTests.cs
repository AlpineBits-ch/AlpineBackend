using Messaging.Application.Integration.Social;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;
using Social.Contracts.Bus.Integration.Events;

namespace Messaging.Tests.Handlers;

/// <summary>Covers FriendRequestAcceptedHandler: notifies the initiator over the hub and rebuilds
/// the conversation-permission cache for both parties so a DM created right after acceptance is
/// immediately visible (see ConversationCreatedHandlerTests' regression-test header comment for
/// the bug this cache rebuild fixes).</summary>
[TestFixture]
public class FriendRequestAcceptedHandlerTests
{
    private TestMessagingContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;
    private ConversationPermissionService _permissionService = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeMessagingHubContext();
        _permissionService = new ConversationPermissionService(_context, _cache);
    }

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
    public async Task Handle_NotifiesInitiatorOverHub()
    {
        var evt = new FriendshipAcceptedEvent { InitiatorUserId = "user-a", AcceptantUserId = "user-b" };

        await FriendRequestAcceptedHandler.Handle(evt, _hub, _permissionService);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Has.Count.EqualTo(1));
        var (method, args) = hubClients.SentMessages[0];
        Assert.Multiple(() =>
        {
            Assert.That(method, Is.EqualTo("conversation.FriendRequestAccepted"));
            Assert.That(args[0], Is.SameAs(evt));
        });
    }

    [Test]
    public async Task Handle_RebuildsPermissionCacheForBothParties()
    {
        _context.Members.AddRange(
            MakeMember("m-1", "user-a", "conv-new"),
            MakeMember("m-2", "user-b", "conv-new"));
        await _context.SaveChangesAsync();

        var evt = new FriendshipAcceptedEvent { InitiatorUserId = "user-a", AcceptantUserId = "user-b" };

        await FriendRequestAcceptedHandler.Handle(evt, _hub, _permissionService);

        Assert.Multiple(async () =>
        {
            Assert.That(await _permissionService.HasPermission("user-a", "conv-new"), Is.True);
            Assert.That(await _permissionService.HasPermission("user-b", "conv-new"), Is.True);
        });
    }
}
