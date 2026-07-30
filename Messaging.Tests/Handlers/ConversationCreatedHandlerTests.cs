using System.Text.Json;
using Echo.Realtime;
using Messaging.Application.Handler.Conversation;

using Messaging.Application.Services;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Conversation;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;

namespace Messaging.Tests.Handlers;

/// <summary>
/// Regression test for the bug where accepting a friend request and then creating a conversation
/// results in a 403 on subsequent message access.
/// </summary>
[TestFixture]
public class ConversationCreatedHandlerTests
{
    private const string InitiatorUserId = "user-a";
    private const string AcceptantUserId = "user-b";
    private const string ConversationId = "conv-new";

    private string _dbName = null!;
    private FakeDistributedCache _cache = null!;
    private TestMessagingContext _context = null!;
    private ConversationPermissionService _permissionService = null!;
    private IHubContext<EchoRealtimeHub> _hubContext = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _cache = new FakeDistributedCache();
        _context = new TestMessagingContext(_dbName);
        _permissionService = new ConversationPermissionService(_context, _cache);
        _hubContext = new FakeMessagingHubContext();
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

  
    
    private static string CacheKey(string userId) =>
        "messaging:conversation-permissions:" + userId;

    private void WarmCacheWithoutNewConversation(string userId)
    {
        // Simulate what FriendRequestAcceptedHandler does: rebuild the cache.
        var stale = new UserConversationPermissions { ConversationIds = [] };
        _cache.SetEntry(CacheKey(userId), JsonSerializer.Serialize(stale));
    }

    /// <summary>
    /// After accepting a friend request the permission cache is warmed (empty conversations).
    /// </summary>
    [Test]
    public async Task Handle_ConversationCreated_RebuildsCacheForAllMembers()
    {
        // Arrange: persist the new conversation + both members to the DB.
        _context.Members.AddRange(
            MakeMember("m-a", InitiatorUserId, ConversationId),
            MakeMember("m-b", AcceptantUserId, ConversationId));
        await _context.SaveChangesAsync();

        // Simulate the warm cache that FriendRequestAcceptedHandler left behind:
        // both users' caches exist but do NOT contain the new conversation.
        WarmCacheWithoutNewConversation(InitiatorUserId);
        WarmCacheWithoutNewConversation(AcceptantUserId);

        var @event = new ConversationCreated { ConversationId = ConversationId };

        // Act: invoke the handler exactly as Wolverine would.
        await ConversationCreatedHandler.Handle(@event, _context, _permissionService, _hubContext);

        // Assert: both members must now be able to access the conversation.
        Assert.Multiple(async () =>
        {
            Assert.That(
                await _permissionService.HasPermission(InitiatorUserId, ConversationId),
                Is.True,
                "Initiator must have permission for the new conversation after the handler runs");

            Assert.That(
                await _permissionService.HasPermission(AcceptantUserId, ConversationId),
                Is.True,
                "Acceptant must have permission for the new conversation after the handler runs");
        });
    }

    [Test]
    public async Task Handle_ConversationCreated_UpdatesCacheEntryForInitiator()
    {
        _context.Members.Add(MakeMember("m-a", InitiatorUserId, ConversationId));
        await _context.SaveChangesAsync();

        WarmCacheWithoutNewConversation(InitiatorUserId);

        var @event = new ConversationCreated { ConversationId = ConversationId };

        await ConversationCreatedHandler.Handle(@event, _context, _permissionService, _hubContext);

        // The cache entry must have been overwritten with fresh data.
        var permissions = await _permissionService.GetPermissionsForUser(InitiatorUserId);
        Assert.That(permissions.ConversationIds, Contains.Item(ConversationId),
            "Cache must contain the new conversation after handler runs");
    }

    [Test]
    public async Task Handle_PendingWelcomesExist_SendsWelcomeToEachOwningUser()
    {
        _context.Members.Add(MakeMember("m-a", InitiatorUserId, ConversationId));
        _context.PendingWelcomes.AddRange(
            new PendingWelcome { Id = "pewe-1", ConversationId = ConversationId, UserId = AcceptantUserId, DeviceId = "device-1", Welcome = [1, 2, 3] },
            new PendingWelcome { Id = "pewe-2", ConversationId = ConversationId, UserId = "user-c", DeviceId = "device-2", Welcome = [4, 5, 6] });
        await _context.SaveChangesAsync();

        var @event = new ConversationCreated { ConversationId = ConversationId };

        await ConversationCreatedHandler.Handle(@event, _context, _permissionService, _hubContext);

        var hubClients = (FakeHubClients)_hubContext.Clients;
        var welcomeMessages = hubClients.SentMessages.Where(m => m.Method == "conversation.Welcome").ToList();
        Assert.That(welcomeMessages, Has.Count.EqualTo(2),
            "Each user with a pending welcome for this conversation must receive one welcome push");
    }
}
