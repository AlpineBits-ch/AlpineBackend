using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Controllers;

/// <summary>
/// Covers MessagingController's two GET history endpoints, now routed through IMessageRepository
/// (see the comment in MessagingController.GetMessages) so both the Scylla and EF Core/Postgres
/// backends behave identically - validation, unauthenticated, and permission-denied paths for
/// both the conversation and channel variants.
/// </summary>
[TestFixture]
public class MessagingControllerTests
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

    private MessagingController MakeController(FakeMessageBus bus, string? userId)
    {
        var controller = new MessagingController(_repo, NullLogger<MessagingController>.Instance, bus, _permissionService);
        var httpContext = new DefaultHttpContext { User = userId is null ? TestPrincipal.Anonymous() : TestPrincipal.ForUser(userId) };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

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

    // ══════════════════════════════════════════════════════════════════════════ GetMessages
    // (conversation) ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetMessages_EmptyConversationId_ReturnsBadRequest()
    {
        var controller = MakeController(new FakeMessageBus(), "user-1");

        var result = await controller.GetMessages("", 0, 10);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetMessages_Unauthenticated_ReturnsUnauthorized()
    {
        var controller = MakeController(new FakeMessageBus(), null);

        var result = await controller.GetMessages("conv-1", 0, 10);

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task GetMessages_UserNotAMember_ReturnsForbid()
    {
        var controller = MakeController(new FakeMessageBus(), "user-1");

        var result = await controller.GetMessages("conv-1", 0, 10);

        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task GetMessages_MemberWithMessages_ReturnsOkWithMessagesAndReactions()
    {
        _context.Members.Add(MakeMember("m-1", "user-1", "conv-1"));
        await _context.SaveChangesAsync();

        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _repo.CreateMessageAsync(message);
        await _repo.AddReactionAsync(Reaction.Create(new CreateReactionParams { MessageId = message.Id, UserId = "user-1", Emoji = "👍", ConversationId = "conv-1" }));
        await _context.SaveChangesAsync();

        var controller = MakeController(new FakeMessageBus(), "user-1");

        var result = await controller.GetMessages("conv-1", 0, 10);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var messages = ((OkObjectResult)result).Value as IEnumerable<MessageDto>;
        var list = messages!.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(list, Has.Count.EqualTo(1));
            Assert.That(list[0].Reactions, Has.Count.EqualTo(1));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetMessagesForChannelAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetMessagesForChannel_EmptyChannelId_ReturnsBadRequest()
    {
        var controller = MakeController(new FakeMessageBus(), "user-1");

        var result = await controller.GetMessagesForChannelAsync("", 0, 10);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetMessagesForChannel_Unauthenticated_ReturnsUnauthorized()
    {
        var controller = MakeController(new FakeMessageBus(), null);

        var result = await controller.GetMessagesForChannelAsync("chan-1", 0, 10);

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task GetMessagesForChannel_PermissionDenied_ReturnsForbid()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var controller = MakeController(bus, "user-1");

        var result = await controller.GetMessagesForChannelAsync("chan-1", 0, 10);

        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task GetMessagesForChannel_PermissionGranted_ReturnsOkWithMessages()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ChannelId = "chan-1", AuthorId = "author-1" });
        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();

        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var controller = MakeController(bus, "user-1");

        var result = await controller.GetMessagesForChannelAsync("chan-1", 0, 10);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var messages = (((OkObjectResult)result).Value as IEnumerable<MessageDto>)!.ToList();
        Assert.That(messages, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════ Paging
    // normalization

    private static FakeMessageBus AllowChannelAccess() => new(msg => msg switch
    {
        HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
        _ => throw new InvalidOperationException("unexpected"),
    });

    [Test]
    public async Task GetMessagesForChannel_OmittedLimit_UsesPositiveDefaultPageSize()
    {
        var mapper = new FakeCassandraMapper
        {
            Messages = [Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ChannelId = "chan-1", AuthorId = "author-1" })],
        };
        var controller = new MessagingController(
            new ScyllaMessageRepository(ScyllaContext.CreateDebug(mapper)),
            NullLogger<MessagingController>.Instance, AllowChannelAccess(), _permissionService);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestPrincipal.ForUser("user-1") } };

        var result = await controller.GetMessagesForChannelAsync("chan-1", 0, 0);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(mapper.Fetches[0].Args[1], Is.EqualTo(50), "LIMIT reaching Scylla must be strictly positive");
    }

    [Test]
    public async Task GetMessagesForChannel_OversizedLimit_IsCapped()
    {
        var mapper = new FakeCassandraMapper();
        var controller = new MessagingController(
            new ScyllaMessageRepository(ScyllaContext.CreateDebug(mapper)),
            NullLogger<MessagingController>.Instance, AllowChannelAccess(), _permissionService);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestPrincipal.ForUser("user-1") } };

        await controller.GetMessagesForChannelAsync("chan-1", 0, 5000);

        Assert.That(mapper.Fetches[0].Args[1], Is.EqualTo(100));
    }
}
