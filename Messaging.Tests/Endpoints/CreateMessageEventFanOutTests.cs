using System.Reflection;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Commands;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Events.Message;
using Messaging.Infrastructure.Persistence;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using CreateMessageCommand = Messaging.Contracts.Bus.Commands.CreateMessageCommand;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Regression cover for the duplicate-notification bug: every message sent through
/// POST /api/v1/messaging notified twice - twice on the phone, and twice in the desktop client.
///
/// <para>Wolverine publishes the *other* members of a handler's returned tuple even when the caller
/// asked for the first one via <c>InvokeAsync&lt;T&gt;</c> (verified against WolverineFx 6.21).
/// So <c>CreateMessageCommandHandler</c> returning <c>(Message, MessageCreated)</c> already raises
/// the event when the endpoint invokes it - and the endpoint then returned a MessageCreated of its
/// own alongside its IResult, which Wolverine.Http cascades as a second, independent publish.
/// MessageCreatedHandler therefore ran twice per send: two SignalR fan-outs and, via Guild's
/// ChannelPushRequested, two FCM/APNs pushes.</para>
///
/// <para>These tests count MessageCreated across the whole create path - the real command handler's
/// cascade plus anything the endpoint returns - rather than asserting on either half, so they hold
/// regardless of which of the two is later changed. The endpoint is called through reflection for
/// the same reason: the test must stay compilable, and keep counting, if the endpoint's return type
/// ever grows a cascaded message again.</para>
/// </summary>
[TestFixture]
public class CreateMessageEventFanOutTests
{
    private TestMessagingContext _context = null!;
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        // NUnit reuses one fixture instance for every test in the class, so a count that is the
        // whole point of these tests has to be reset explicitly.
        _emitted.Clear();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Every MessageCreated one call to the endpoint produced, from either publisher.</summary>
    private readonly List<MessageCreated> _emitted = [];

    /// <summary>
    /// A bus that runs the REAL CreateMessageCommandHandler for a CreateMessageCommand, recording
    /// the event it cascades. Faking the handler here would hide half the fan-out - the half that
    /// is supposed to survive.
    /// </summary>
    private FakeMessageBus MakeBus(bool permissionAllowed = true) => new(msg => msg switch
    {
        HasUserPermissionToChannelRequest r =>
            new HasUserPermissionToChannelResponse { IsAllowed = permissionAllowed, Permission = r.Permission },
        GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
        CreateMessageCommand cmd => RunRealHandler(cmd),
        _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
    });

    private Message RunRealHandler(CreateMessageCommand command)
    {
        var (message, evt) = new CreateMessageCommandHandler()
            .Handle(command, new EfCoreMessageRepository(_context), _context)
            .GetAwaiter().GetResult();

        _emitted.Add(evt);
        return message;
    }

    private MlsGroupService MakeMlsService(FakeMessageBus bus) =>
        new(_context, new FakeMessagingHubContext(), bus, new MlsJoinRequestService(_context),
            TestMlsServices.Coverage(bus));

    /// <summary>
    /// Invokes the endpoint through reflection and folds anything it returns into the same count.
    /// A bare IResult contributes nothing; a tuple contributes every DomainEvent in it - which is
    /// precisely how the second publish used to arrive.
    /// </summary>
    private async Task InvokeEndpointAsync(CreateMessageDto dto, FakeMessageBus bus, string userId)
    {
        var method = typeof(MessagingEndpoints).GetMethod(nameof(MessagingEndpoints.CreateMessage))!;

        var task = (Task)method.Invoke(new MessagingEndpoints(),
        [
            dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser(userId), _context, bus, _cache,
            MakeMlsService(bus),
            TestPrivacyServices.Build(bus).Policy,
            TestPrivacyServices.Build(bus).Content,
        ])!;

        await task;

        var returned = task.GetType().GetProperty("Result")?.GetValue(task);
        if (returned is null) return;

        foreach (var field in returned.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.GetValue(returned) is MessageCreated cascaded) _emitted.Add(cascaded);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Normal paths - one send, one notification
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateMessage_ChannelScope_EmitsExactlyOneMessageCreated()
    {
        var bus = MakeBus();

        await InvokeEndpointAsync(new CreateMessageDto { Content = "hello", ChannelId = "chan-1" }, bus, "user-1");

        Assert.That(_emitted, Has.Count.EqualTo(1),
            "a second MessageCreated is a second realtime fan-out and a second phone push for one message");
    }

    [Test]
    public async Task CreateMessage_ConversationScope_EmitsExactlyOneMessageCreated()
    {
        await SeedConversationWithMember("conv-1", "user-1");
        var bus = MakeBus();

        await InvokeEndpointAsync(new CreateMessageDto { Content = "hi", ConversationId = "conv-1" }, bus, "user-1");

        Assert.That(_emitted, Has.Count.EqualTo(1));
    }

    /// <summary>Two sends are two notifications - the assertion above must not be satisfied by a
    /// path that drops events on the floor.</summary>
    [Test]
    public async Task CreateMessage_TwoSends_EmitOneMessageCreatedEach()
    {
        var bus = MakeBus();

        await InvokeEndpointAsync(new CreateMessageDto { Content = "one", ChannelId = "chan-1" }, bus, "user-1");
        await InvokeEndpointAsync(new CreateMessageDto { Content = "two", ChannelId = "chan-1" }, bus, "user-1");

        Assert.Multiple(() =>
        {
            Assert.That(_emitted, Has.Count.EqualTo(2));
            Assert.That(_emitted.Select(e => e.MessageId).Distinct().Count(), Is.EqualTo(2),
                "two distinct messages, not one message announced twice");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Negative path - a rejected send notifies nobody
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateMessage_WithoutSendPermission_EmitsNoMessageCreated()
    {
        var bus = MakeBus(permissionAllowed: false);

        await InvokeEndpointAsync(new CreateMessageDto { Content = "nope", ChannelId = "chan-1" }, bus, "user-1");

        Assert.That(_emitted, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The event that survives has to be the complete one
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ContextId and CorrelationId used to be set only on the duplicate the endpoint raised, so
    /// removing that duplicate had to move them onto the surviving event rather than lose them.
    /// </summary>
    [Test]
    public async Task CreateMessage_TheSurvivingEvent_CarriesTheContextAndTimestamp()
    {
        var bus = MakeBus();

        await InvokeEndpointAsync(
            new CreateMessageDto { Content = "hello", ChannelId = "chan-1", Mentions = ["user-2"], RoleMentions = ["role-1"] },
            bus, "user-1");

        var evt = _emitted.Single();
        Assert.Multiple(() =>
        {
            Assert.That(evt.ContextId, Is.EqualTo("chan-1"));
            Assert.That(evt.CorrelationId, Is.EqualTo("chan-1"));
            Assert.That(evt.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(evt.AuthorId, Is.EqualTo("user-1"));
            Assert.That(evt.Mentions, Is.EqualTo(new[] { "user-2" }).AsCollection);
            Assert.That(evt.RoleMentions, Is.EqualTo(new[] { "role-1" }).AsCollection);
            Assert.That(evt.CreatedAt, Is.Not.EqualTo(default(DateTimeOffset)),
                "an unset CreatedAt reaches Guild as year 1 and lands on Channel.LastActivityAt");
        });
    }

    private async Task SeedConversationWithMember(string conversationId, string userId)
    {
        _context.Conversations.Add(new Conversation
        {
            Id = conversationId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Members =
            [
                new ConversationMember
                {
                    Id = "cm-1",
                    UserId = userId,
                    ConversationId = conversationId,
                    PublicKey = [],
                    CachedUserName = "test-user",
                    CachedUserHash = 0,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        });
        await _context.SaveChangesAsync();
    }
}
