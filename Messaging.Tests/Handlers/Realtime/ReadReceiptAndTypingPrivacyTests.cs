using Echo.Realtime;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Application.Handler.Realtime;
using Messaging.Application.Services.Privacy;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ConversationDto = Messaging.Application.Dtos.Response.ConversationDto;

namespace Messaging.Tests.Handlers.Realtime;

/// <summary>
/// T2-18. Read receipts and typing indicators, both default on, both <b>reciprocal</b> - a user who
/// does not send them does not receive them - and both enforced where the fact leaves the server
/// rather than where a client would draw it.
/// </summary>
[TestFixture]
public class ReadReceiptAndTypingPrivacyTests
{
    private TestMessagingContext _context = null!;
    private FakeMessagingHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _hub = new FakeMessagingHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private FakeHubClients Clients => (FakeHubClients)_hub.Clients;

    private static PrivacySettingsCache Privacy(params UserPrivacySettingsSummary[] settings) =>
        TestPrivacyServices.Build(new FakeMessageBus(), settings).Privacy;

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

    private async Task SeedConversation(params string[] userIds)
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Members = userIds.Select((u, i) => MakeMember($"m-{i}", u, "conv-1")).ToList(),
        });
        await _context.SaveChangesAsync();
    }

    // ── Typing ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Typing_WithTheDefaultsOn_ReachesEveryone()
    {
        await SeedConversation("user-1", "user-2");

        await StartConversationTypingHandler.Handle(
            new StartConversationTypingCommand("user-1", "conv-1"), _context, Privacy(), _hub);

        Assert.That(Clients.Sends.Select(s => s.Target),
            Is.EquivalentTo(new[] { "user:user-1", "user:user-2" }));
    }

    [Test]
    public async Task Typing_IsNotEmittedAtAllByAUserWhoTurnedItOff()
    {
        await SeedConversation("user-1", "user-2");

        await StartConversationTypingHandler.Handle(
            new StartConversationTypingCommand("user-1", "conv-1"), _context,
            Privacy(TestPrivacyServices.With("user-1", s => s.SendTypingIndicators = false)), _hub);

        Assert.That(Clients.Sends.Select(s => s.Target), Is.EquivalentTo(new[] { "user:user-1" }),
            "The typist's own echo is theirs; nobody else hears about it");
    }

    [Test]
    public async Task Typing_IsNotDeliveredToAUserWhoTurnedItOff()
    {
        // The reciprocity half: user-2 withholds their own typing state, so they do not get to
        // watch user-1's.
        await SeedConversation("user-1", "user-2", "user-3");

        await StartConversationTypingHandler.Handle(
            new StartConversationTypingCommand("user-1", "conv-1"), _context,
            Privacy(TestPrivacyServices.With("user-2", s => s.SendTypingIndicators = false)), _hub);

        Assert.That(Clients.Sends.Select(s => s.Target),
            Is.EquivalentTo(new[] { "user:user-1", "user:user-3" }));
    }

    [Test]
    public async Task Typing_WhenPrivacyCannotBeResolved_IsSuppressed()
    {
        // The fail-closed defaults have SendTypingIndicators false, which is the restrictive
        // direction: no indicator is emitted rather than one being leaked.
        await SeedConversation("user-1", "user-2");

        var privacy = TestPrivacyServices.Build(new FakeMessageBus(), privacyLookupFails: true).Privacy;

        await StartConversationTypingHandler.Handle(
            new StartConversationTypingCommand("user-1", "conv-1"), _context, privacy, _hub);

        Assert.That(Clients.Sends.Select(s => s.Target), Is.EquivalentTo(new[] { "user:user-1" }));
    }

    // ── Read receipts ─────────────────────────────────────────────────────────

    [Test]
    public async Task ReadReceipt_WithTheDefaultsOn_IsEmittedToPeers()
    {
        await SeedConversation("user-1", "user-2");

        await UpdateConversationReadHandler.Handle(
            new UpdateConversationReadCommand("user-1", "conv-1", "msg-9"), _context, Privacy(), _hub);

        Assert.Multiple(async () =>
        {
            Assert.That(Clients.Sends.Select(s => (s.Target, s.Method)),
                Is.EquivalentTo(new[] { ("user:user-2", UpdateConversationReadHandler.ReadReceiptEvent) }));
            var member = await _context.Members.FindAsync("m-0");
            Assert.That(member!.LastReadMessageId, Is.EqualTo("msg-9"));
        });
    }

    [Test]
    public async Task ReadReceipt_IsStillRecordedForAUserWhoDoesNotSendThem()
    {
        // The setting is about telling other people. Taking away the user's own unread state would
        // be charging them for their own privacy.
        await SeedConversation("user-1", "user-2");

        await UpdateConversationReadHandler.Handle(
            new UpdateConversationReadCommand("user-1", "conv-1", "msg-9"), _context,
            Privacy(TestPrivacyServices.With("user-1", s => s.SendReadReceipts = false)), _hub);

        var member = await _context.Members.FindAsync("m-0");

        Assert.Multiple(() =>
        {
            Assert.That(member!.LastReadMessageId, Is.EqualTo("msg-9"));
            Assert.That(Clients.Sends, Is.Empty);
        });
    }

    [Test]
    public async Task ReadReceipt_IsNotDeliveredToAUserWhoDoesNotSendThem()
    {
        await SeedConversation("user-1", "user-2", "user-3");

        await UpdateConversationReadHandler.Handle(
            new UpdateConversationReadCommand("user-1", "conv-1", "msg-9"), _context,
            Privacy(TestPrivacyServices.With("user-2", s => s.SendReadReceipts = false)), _hub);

        Assert.That(Clients.Sends.Select(s => s.Target), Is.EquivalentTo(new[] { "user:user-3" }));
    }

    // ── The projection, which is the other half of a read receipt ─────────────

    private ConversationController Controller(string viewerUserId, PrivacySettingsCache privacy)
    {
        var controller = new ConversationController(_context, privacy);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = TestPrincipal.ForUser(viewerUserId) },
        };
        return controller;
    }

    [Test]
    public async Task Projection_HidesTheReadPositionOfAMemberWhoDoesNotSendReceipts()
    {
        await SeedConversation("user-1", "user-2");
        var other = await _context.Members.FindAsync("m-1");
        other!.LastReadMessageId = "msg-9";
        await _context.SaveChangesAsync();

        var controller = Controller("user-1",
            Privacy(TestPrivacyServices.With("user-2", s => s.SendReadReceipts = false)));

        var response = (OkObjectResult)await controller.GetConversation("conv-1");
        var dto = (ConversationDto)response.Value!;

        Assert.That(dto.Members.Single(m => m.UserId == "user-2").LastReadMessageId, Is.Null,
            "Otherwise the emit-site gate is defeated by polling the conversation list");
    }

    [Test]
    public async Task Projection_HidesEveryoneElsesReadPositionFromAViewerWhoDoesNotSendReceipts()
    {
        await SeedConversation("user-1", "user-2");
        var other = await _context.Members.FindAsync("m-1");
        other!.LastReadMessageId = "msg-9";
        await _context.SaveChangesAsync();

        var controller = Controller("user-1",
            Privacy(TestPrivacyServices.With("user-1", s => s.SendReadReceipts = false)));

        var response = (OkObjectResult)await controller.GetConversation("conv-1");
        var dto = (ConversationDto)response.Value!;

        Assert.That(dto.Members.Single(m => m.UserId == "user-2").LastReadMessageId, Is.Null);
    }

    [Test]
    public async Task Projection_NeverBlanksTheCallersOwnReadPosition()
    {
        // It is their own state and the client needs it to place the unread divider.
        await SeedConversation("user-1", "user-2");
        var mine = await _context.Members.FindAsync("m-0");
        mine!.LastReadMessageId = "msg-7";
        await _context.SaveChangesAsync();

        var controller = Controller("user-1",
            Privacy(TestPrivacyServices.With("user-1", s => s.SendReadReceipts = false)));

        var response = (OkObjectResult)await controller.GetConversation("conv-1");
        var dto = (ConversationDto)response.Value!;

        Assert.That(dto.Members.Single(m => m.UserId == "user-1").LastReadMessageId, Is.EqualTo("msg-7"));
    }

    [Test]
    public async Task Projection_LeavesReadPositionsAloneWhenBothSidesSendReceipts()
    {
        await SeedConversation("user-1", "user-2");
        var other = await _context.Members.FindAsync("m-1");
        other!.LastReadMessageId = "msg-9";
        await _context.SaveChangesAsync();

        var controller = Controller("user-1", Privacy());

        var response = (OkObjectResult)await controller.GetConversation("conv-1");
        var dto = (ConversationDto)response.Value!;

        Assert.That(dto.Members.Single(m => m.UserId == "user-2").LastReadMessageId, Is.EqualTo("msg-9"));
    }
}
