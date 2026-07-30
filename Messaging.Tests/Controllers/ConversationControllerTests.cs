using System.Security.Claims;
using Messaging.Application.Controllers;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using ConversationDto = global::Messaging.Application.Dtos.Response.ConversationDto;
using PendingWelcomeDto = global::Messaging.Application.Dtos.Request.PendingWelcomeDto;

namespace Messaging.Tests.Controllers;

/// <summary>
/// Covers ConversationController's plain MVC actions (GetConversations/GetConversation/
/// GetWelcomeMessages) - unlike the Wolverine-attributed endpoints, this is a real
/// ControllerBase, so its User principal must be wired up manually via ControllerContext.
/// </summary>
[TestFixture]
public class ConversationControllerTests
{
    private TestMessagingContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestMessagingContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ConversationController MakeController(TestMessagingContext ctx, ClaimsPrincipal user)
    {
        var controller = new ConversationController(ctx);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user },
        };
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

    // ══════════════════════════════════════════════════════════════════════════ GetConversations
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetConversations_Unauthenticated_ReturnsBadRequest()
    {
        var controller = MakeController(_context, TestPrincipal.Anonymous());

        var result = await controller.GetConversations(0, 20);

        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task GetConversations_ReturnsOnlyConversationsUserIsMemberOf()
    {
        _context.Conversations.AddRange(
            new Conversation { Id = "conv-1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Members = [MakeMember("m-1", "user-1", "conv-1")] },
            new Conversation { Id = "conv-2", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Members = [MakeMember("m-2", "other-user", "conv-2")] });
        await _context.SaveChangesAsync();

        var controller = MakeController(_context, TestPrincipal.ForUser("user-1"));

        var result = await controller.GetConversations(0, 20);

        var ok = (OkObjectResult)result;
        var conversations = ((IEnumerable<ConversationDto>)ok.Value!).ToList();
        Assert.That(conversations.Select(c => c.Id), Is.EquivalentTo(new[] { "conv-1" }));
    }

    [Test]
    public async Task GetConversations_RespectsOffsetAndLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            _context.Conversations.Add(new Conversation
            {
                Id = $"conv-{i}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Members = [MakeMember($"m-{i}", "user-1", $"conv-{i}")],
            });
        }
        await _context.SaveChangesAsync();

        var controller = MakeController(_context, TestPrincipal.ForUser("user-1"));

        var result = await controller.GetConversations(2, 2);

        var ok = (OkObjectResult)result;
        var conversations = ((IEnumerable<ConversationDto>)ok.Value!).ToList();
        Assert.That(conversations, Has.Count.EqualTo(2));
    }

    // ══════════════════════════════════════════════════════════════════════════ GetWelcomeMessages
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetWelcomeMessages_Unauthenticated_ReturnsBadRequest()
    {
        var controller = MakeController(_context, TestPrincipal.Anonymous());

        var result = await controller.GetWelcomeMessages();

        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task GetWelcomeMessages_ReturnsAndConsumesPendingWelcomes()
    {
        _context.PendingWelcomes.Add(new PendingWelcome { Id = "pewe-1", ConversationId = "conv-1", UserId = "user-1", DeviceId = "device-1", Welcome = [1, 2, 3] });
        await _context.SaveChangesAsync();

        var controller = MakeController(_context, TestPrincipal.ForUser("user-1"));

        var result = await controller.GetWelcomeMessages();

        var ok = (OkObjectResult)result;
        var welcomes = ((IEnumerable<PendingWelcomeDto>)ok.Value!).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(welcomes, Has.Count.EqualTo(1));
            Assert.That(_context.PendingWelcomes.Any(), Is.False, "Fetched welcomes must be consumed (removed) so they aren't redelivered");
        });
    }

    [Test]
    public async Task GetWelcomeMessages_OnlyReturnsWelcomesForRequestingUser()
    {
        _context.PendingWelcomes.AddRange(
            new PendingWelcome { Id = "pewe-1", ConversationId = "conv-1", UserId = "user-1", DeviceId = "device-1", Welcome = [1] },
            new PendingWelcome { Id = "pewe-2", ConversationId = "conv-1", UserId = "other-user", DeviceId = "device-2", Welcome = [2] });
        await _context.SaveChangesAsync();

        var controller = MakeController(_context, TestPrincipal.ForUser("user-1"));

        await controller.GetWelcomeMessages();

        Assert.That(_context.PendingWelcomes.Any(w => w.UserId == "other-user"), Is.True,
            "Other users' pending welcomes must be untouched");
    }

    // ══════════════════════════════════════════════════════════════════════════ GetConversation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetConversation_Unauthenticated_ReturnsBadRequest()
    {
        var controller = MakeController(_context, TestPrincipal.Anonymous());

        var result = await controller.GetConversation("conv-1");

        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task GetConversation_NotFoundOrNotAMember_ReturnsNotFound()
    {
        _context.Conversations.Add(new Conversation { Id = "conv-1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Members = [MakeMember("m-1", "other-user", "conv-1")] });
        await _context.SaveChangesAsync();

        var controller = MakeController(_context, TestPrincipal.ForUser("user-1"));

        var result = await controller.GetConversation("conv-1");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetConversation_UserIsMember_ReturnsConversation()
    {
        _context.Conversations.Add(new Conversation { Id = "conv-1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Members = [MakeMember("m-1", "user-1", "conv-1")] });
        await _context.SaveChangesAsync();

        var controller = MakeController(_context, TestPrincipal.ForUser("user-1"));

        var result = await controller.GetConversation("conv-1");

        var ok = (OkObjectResult)result;
        var dto = (ConversationDto)ok.Value!;
        Assert.That(dto.Id, Is.EqualTo("conv-1"));
    }
}
