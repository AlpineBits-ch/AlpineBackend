using Microsoft.AspNetCore.Http;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Endpoints;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Conversation;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using ConversationDto = Messaging.Application.Dtos.Response.ConversationDto;
using ContractMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Messaging.Tests.Endpoints;

/// <summary>Covers UpdateConversation: who may rename, what a blank name means, and the notice and
/// push a rename leaves behind.</summary>
[TestFixture]
public class ConversationRenameTests
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

    private async Task SeedAsync(string id, string? name, params string[] memberUserIds)
    {
        _context.Conversations.Add(new Conversation
        {
            Id = id,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = memberUserIds.Select((u, i) => MakeMember($"m-{i}", u, id)).ToList(),
        });
        await _context.SaveChangesAsync();
    }

    private Task<(IResult, ConversationUpdated?)> Rename(string id, string? name, string callerUserId, FakeMessageBus bus) =>
        ConversationEndpoints.UpdateConversation(
            id, new UpdateConversationDto { Name = name }, bus, TestPrincipal.ForUser(callerUserId), _context);

    [Test]
    public async Task Unauthenticated_ReturnsUnauthorized()
    {
        var (result, _) = await ConversationEndpoints.UpdateConversation(
            "conv-1", new UpdateConversationDto { Name = "x" }, new FakeMessageBus(),
            TestPrincipal.Anonymous(), _context);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task NonMember_IsForbidden()
    {
        await SeedAsync("conv-1", null, "user-1", "user-2", "user-3");

        var (result, _) = await Rename("conv-1", "New", "outsider", new FakeMessageBus());

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DirectMessage_IsRefused()
    {
        await SeedAsync("conv-1", null, "user-1", "user-2");

        var (result, evt) = await Rename("conv-1", "New", "user-1", new FakeMessageBus());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public async Task AnyMemberMayRename_AndTheNameIsTrimmed()
    {
        await SeedAsync("conv-1", null, "user-1", "user-2", "user-3");
        var bus = new FakeMessageBus();

        var (result, evt) = await Rename("conv-1", "  Die Gummibaerenbande  ", "user-3", bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
            Assert.That(_context.Conversations.Single().Name, Is.EqualTo("Die Gummibaerenbande"));
            Assert.That(evt!.Name, Is.EqualTo("Die Gummibaerenbande"));
        });
    }

    [Test]
    public async Task BlankName_ClearsIt()
    {
        await SeedAsync("conv-1", "Old", "user-1", "user-2", "user-3");

        var (result, evt) = await Rename("conv-1", "   ", "user-1", new FakeMessageBus());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
            Assert.That(_context.Conversations.Single().Name, Is.Null);
            Assert.That(evt!.Name, Is.Null);
        });
    }

    [Test]
    public async Task OverLengthName_IsRefused()
    {
        await SeedAsync("conv-1", null, "user-1", "user-2", "user-3");

        var (result, _) = await Rename("conv-1", new string('a', ConversationEndpoints.MaxConversationNameLength + 1), "user-1", new FakeMessageBus());

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UnchangedName_WritesNothing()
    {
        await SeedAsync("conv-1", "Same", "user-1", "user-2", "user-3");
        var bus = new FakeMessageBus();

        var (result, evt) = await Rename("conv-1", "Same", "user-1", bus);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
            // No notice and no push: re-saving the dialog unchanged must not spam the group.
            Assert.That(evt, Is.Null);
            Assert.That(bus.Invoked, Is.Empty);
        });
    }

    [Test]
    public async Task Rename_LeavesANoticeCarryingTheNewName()
    {
        await SeedAsync("conv-1", null, "user-1", "user-2", "user-3");
        var bus = new FakeMessageBus();

        await Rename("conv-1", "Weekend", "user-2", bus);

        var command = bus.Invoked.OfType<CreateMessageCommand>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(command.Type, Is.EqualTo(ContractMessageType.GroupNameChanged));
            Assert.That(command.AuthorId, Is.EqualTo("user-2"));
            Assert.That(System.Text.Encoding.UTF8.GetString(command.Content), Is.EqualTo("Weekend"));
        });
    }

    [Test]
    public async Task ClearingTheName_LeavesANoticeWithEmptyContent()
    {
        await SeedAsync("conv-1", "Old", "user-1", "user-2", "user-3");
        var bus = new FakeMessageBus();

        await Rename("conv-1", null, "user-1", bus);

        var command = bus.Invoked.OfType<CreateMessageCommand>().Single();

        Assert.That(command.Content, Is.Empty);
    }
}
