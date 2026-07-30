using Guild.Contracts;
using Guild.Contracts.Bus.Events;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Commands;
using Messaging.Contracts.Bus.Response;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using PinMessageCommand = Messaging.Contracts.Bus.Commands.PinMessageCommand;
using UnpinMessageCommand = Messaging.Contracts.Bus.Commands.UnpinMessageCommand;
using UpdateMessageCommand = Messaging.Contracts.Bus.Commands.UpdateMessageCommand;
using MessageDto = global::Messaging.Application.Dtos.Response.MessageDto;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Covers MessagingEndpoints: CreateMessage's channel-permission/automod/conversation-membership
/// gating, DeleteMessage's author-only check, UpdateMessageAsync/PinMessage/UnpinMessage/
/// GetPinnedMessages' auth and not-found/forbidden routing. The bus is faked throughout, so these
/// exercise the endpoint's own control flow rather than what CreateMessageCommandHandler/
/// PinMessageCommandHandler/etc. do internally (covered separately in Commands/*Tests.cs).
/// </summary>
[TestFixture]
public class MessagingEndpointsTests
{
    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;
    private FakeDistributedCache _cache = null!;
    private ConversationPermissionService _permissionService = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
        _cache = new FakeDistributedCache();
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

    private static Message FakeHandlerReturnFor(CreateMessageCommand cmd) => Message.Create(new CreateMessageParams
    {
        Content = cmd.Content,
        ChannelId = cmd.ChannelId,
        ConversationId = cmd.ConversationId,
        AuthorId = cmd.AuthorId,
    });

    // ══════════════════════════════════════════════════════════════════════════
    // CreateMessage
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateMessageDto { Content = "hi", ConversationId = "conv-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.Anonymous(), _context, bus, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_NoChannelOrConversationId_ReturnsBadRequest()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateMessageDto { Content = "hi" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_LacksSendMessagesPermission_ReturnsForbid()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateMessageDto { Content = "hi", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_AutoModBlocksWord_ReturnsForbiddenJson_AndPublishesAutoModTriggered()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = true, BlockedWords = ["badword"] },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateMessageDto { Content = "this has a badword in it", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<IStatusCodeHttpResult>());
            Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(403));
            Assert.That(evt, Is.Null);
            Assert.That(bus.Published.Any(p => p is AutoModTriggeredEvent), Is.True);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_BotAuthor_BypassesAutoMod_EvenIfWordWouldBeBlocked()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            // No GetGuildAutoModConfigRequest branch: a Bot author must never reach AutoModeration.CheckAsync.
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "badword", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("bot-1", userType: "Bot"), _context, bus, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(evt, Is.Not.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ChannelScope_AllowedAndClean_ReturnsCreated()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "hello world", ChannelId = "chan-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(evt.AuthorId, Is.EqualTo("user-1"));
        });
    }

    [Test]
    public async Task CreateMessage_ConversationScope_ConversationDoesNotExist_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateMessageDto { Content = "hi", ConversationId = "conv-missing" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFound>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ConversationScope_UserNotAMember_ReturnsForbid()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = [MakeMember("m-1", "other-user", "conv-1")],
        });
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateMessageDto { Content = "hi", ConversationId = "conv-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task CreateMessage_ConversationScope_UserIsMember_ReturnsCreated()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = [MakeMember("m-1", "user-1", "conv-1")],
        });
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            CreateMessageCommand cmd => FakeHandlerReturnFor(cmd),
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        var dto = new CreateMessageDto { Content = "hi", ConversationId = "conv-1" };

        var (result, evt) = await endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser("user-1"), _context, bus, _cache);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.ConversationId, Is.EqualTo("conv-1"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DeleteMessage
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();

        var (result, evt) = await endpoint.DeleteMessage("msg-1", _repo, TestPrincipal.Anonymous());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task DeleteMessage_MessageDoesNotExist_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();

        var (result, evt) = await endpoint.DeleteMessage("nope", _repo, TestPrincipal.ForUser("user-1"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFound>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task DeleteMessage_RequesterIsNotAuthor_ReturnsForbid()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();

        var (result, evt) = await endpoint.DeleteMessage(message.Id, _repo, TestPrincipal.ForUser("someone-else"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task DeleteMessage_AuthorDeletesOwnMessage_ReturnsAccepted()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();
        // GetMessageAsync (inside DeleteMessage) reads via AsNoTracking, producing a second
        // instance sharing this key - without clearing the tracker first, DeleteMessageAsync's
        // later Remove() throws EF's identity-conflict exception (test-setup artifact, see the
        // matching comment in PinMessageCommandTests.SeedMessage).
        _context.ChangeTracker.Clear();

        var endpoint = new MessagingEndpoints();

        var (result, evt) = await endpoint.DeleteMessage(message.Id, _repo, TestPrincipal.ForUser("author-1"));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Accepted>());
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt!.MessageId, Is.EqualTo(message.Id));
        });
        Assert.That(_context.Messages.Any(m => m.Id == message.Id), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // UpdateMessageAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.UpdateMessageAsync("msg-1", new UpdateMessageDto { Content = "new" }, TestPrincipal.Anonymous(), bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UpdateMessage_HandlerReportsNotFound_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            UpdateMessageCommand => new UpdateMessageResponse { NotFound = true },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.UpdateMessageAsync("msg-1", new UpdateMessageDto { Content = "new" }, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateMessage_HandlerReportsForbidden_ReturnsForbid()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            UpdateMessageCommand => new UpdateMessageResponse { Forbidden = true },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.UpdateMessageAsync("msg-1", new UpdateMessageDto { Content = "new" }, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateMessage_Success_ReturnsAccepted()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            UpdateMessageCommand => new UpdateMessageResponse { Success = true },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.UpdateMessageAsync("msg-1", new UpdateMessageDto { Content = "new" }, TestPrincipal.ForUser("user-1"), bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<IStatusCodeHttpResult>());
            Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(202));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PinMessage / UnpinMessage
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PinMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.PinMessage("msg-1", _repo, TestPrincipal.Anonymous(), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task PinMessage_MessageDoesNotExist_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.PinMessage("nope", _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task PinMessage_ChannelMessage_LacksPinPermission_ReturnsForbid()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ChannelId = "chan-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.PinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task PinMessage_ConversationMessage_UserLacksMembership_ReturnsForbid()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.PinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task PinMessage_ConversationMessage_MemberWithPermission_ReturnsOkResult()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            PinMessageCommand => new PinMessageResponse { Success = true, PinnedById = "user-1" },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.PinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<Ok<PinMessageResponse>>());
    }

    [Test]
    public async Task PinMessage_HandlerReportsNotFound_ReturnsNotFound()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            PinMessageCommand => new PinMessageResponse { NotFound = true },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.PinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UnpinMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.UnpinMessage("msg-1", _repo, TestPrincipal.Anonymous(), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UnpinMessage_MessageDoesNotExist_ReturnsNotFound()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.UnpinMessage("nope", _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UnpinMessage_ChannelMessage_HasPermission_ReturnsOk()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ChannelId = "chan-1", AuthorId = "author-1" });
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            UnpinMessageCommand => new PinMessageResponse { Success = true },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

        var result = await endpoint.UnpinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<Ok<PinMessageResponse>>());
    }

    [Test]
    public async Task UnpinMessage_MessageHasNeitherChannelNorConversation_ReturnsNotFound()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-x", AuthorId = "author-1" });
        message.ConversationId = null;
        message.ChannelId = null;
        await _context.Messages.AddAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.UnpinMessage(message.Id, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetPinnedMessages
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetPinnedMessages_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.GetPinnedMessages("chan-1", null, _repo, TestPrincipal.Anonymous(), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetPinnedMessages_NoChannelOrConversationId_ReturnsBadRequest()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.GetPinnedMessages(null, null, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<BadRequest>());
    }

    [Test]
    public async Task GetPinnedMessages_ChannelScope_LacksViewPermission_ReturnsForbid()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.GetPinnedMessages("chan-1", null, _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetPinnedMessages_ConversationScope_UserLacksPermission_ReturnsForbid()
    {
        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.GetPinnedMessages(null, "conv-1", _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetPinnedMessages_ConversationScope_UserIsMember_ReturnsPinnedMessages()
    {
        var pinned = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        pinned.IsPinned = true;
        pinned.PinnedAt = DateTime.UtcNow;
        pinned.PinnedById = "user-1";
        await _context.Messages.AddAsync(pinned);
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var endpoint = new MessagingEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.GetPinnedMessages(null, "conv-1", _repo, TestPrincipal.ForUser("user-1"), _permissionService, bus);

        Assert.That(result, Is.InstanceOf<Ok<IEnumerable<MessageDto>>>());
        var ok = (Ok<IEnumerable<MessageDto>>)result;
        Assert.That(ok.Value!.Select(m => m.Id), Contains.Item(pinned.Id));
    }
}
