using System.Text;
using Echo.Realtime.Caching;
using Messaging.Application.Handler.Call;
using Messaging.Application.Services;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;
using Messaging.Tests.Helpers;
using CallEntity = Messaging.Domain.Entities.Call;
using CommandMessageType = Messaging.Contracts.Bus.Commands.MessageType;

// Namespace matches the sibling call-handler fixtures rather than the folder: a
// `Messaging.Tests.Handlers.Call` namespace shadows the `Call` entity for every file in it.
namespace Messaging.Tests.Handlers;

/// <summary>
/// Covers what <see cref="CallEndedHandler"/> does for the conversation a call belonged to: the
/// history entry it leaves behind, the notification that retracts the "join the call" affordance,
/// and the state it clears.
/// </summary>
[TestFixture]
public class CallEndedConversationTests
{
    private const string CallId = "call-1";
    private const string ConversationId = "conv-1";
    private const string Caller = "user-caller";
    private const string Callee = "user-callee";
    private const string Bystander = "user-bystander";

    private FakeDistributedCache _cache = null!;
    private FakeMessagingHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private StreamViewerStore _viewers = null!;
    private TestMessagingContext _context = null!;

    private FakeHubClients HubClients => (FakeHubClients)_hub.Clients;

    [SetUp]
    public async Task SetUp()
    {
        _cache = new FakeDistributedCache();
        _hub = new FakeMessagingHubContext();
        _bus = new FakeMessageBus();
        _viewers = new StreamViewerStore(new FakeDistributedLockService(), _cache);
        _context = new TestMessagingContext(Guid.NewGuid().ToString());

        // The caller, the callee, and someone in the conversation who was never on the call - the
        // person the "a call is happening, join it" affordance exists for.
        foreach (var (id, userId) in new[] { ("m-1", Caller), ("m-2", Callee), ("m-3", Bystander) })
        {
            _context.Members.Add(new ConversationMember
            {
                Id = id, UserId = userId, ConversationId = ConversationId,
                PublicKey = [], CachedUserName = userId, CachedUserHash = 0,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private Task HandleAsync(CallEnded @event) =>
        CallEndedHandler.Handle(@event, _cache, _viewers, _hub, _context, _bus);

    private static CallEnded Event(bool answered = true, DateTimeOffset? startedAt = null) => new()
    {
        CallId = CallId,
        ConversationId = ConversationId,
        CreatorId = Caller,
        ParticipantIds = [Caller, Callee],
        StartedAt = startedAt ?? DateTimeOffset.UtcNow,
        Answered = answered,
        Reason = CallEndReason.UserEnded,
    };

    private CreateMessageCommand? HistoryEntry() =>
        _bus.Invoked.OfType<CreateMessageCommand>().SingleOrDefault();

    // ══════════════════════════════════════════════════════════════════════════ History entry
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AnsweredCall_LeavesAnEndedEntryAuthoredByTheCaller()
    {
        await HandleAsync(Event(answered: true, startedAt: DateTimeOffset.UtcNow.AddSeconds(-125)));

        var entry = HistoryEntry();
        Assert.That(entry, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(entry!.Type, Is.EqualTo(CommandMessageType.CallEnded));
            Assert.That(entry.ConversationId, Is.EqualTo(ConversationId));
            Assert.That(entry.AuthorId, Is.EqualTo(Caller),
                "authorship is what lets a client render 'X called' without a second lookup");
        });
    }

    [Test]
    public async Task AnsweredCall_CarriesItsDurationInSeconds()
    {
        await HandleAsync(Event(answered: true, startedAt: DateTimeOffset.UtcNow.AddSeconds(-125)));

        var seconds = long.Parse(Encoding.UTF8.GetString(HistoryEntry()!.Content));
        Assert.That(seconds, Is.InRange(120, 130));
    }

    [Test]
    public async Task ClockSkew_NeverYieldsANegativeDuration()
    {
        // StartedAt comes off the aggregate and "now" off this machine.
        await HandleAsync(Event(answered: true, startedAt: DateTimeOffset.UtcNow.AddSeconds(30)));

        var seconds = long.Parse(Encoding.UTF8.GetString(HistoryEntry()!.Content));
        Assert.That(seconds, Is.Zero);
    }

    [Test]
    public async Task UnansweredCall_LeavesAMissedEntryWithNoDuration()
    {
        await HandleAsync(Event(answered: false));

        var entry = HistoryEntry()!;
        Assert.Multiple(() =>
        {
            Assert.That(entry.Type, Is.EqualTo(CommandMessageType.CallMissed));
            Assert.That(entry.Content, Is.Empty, "there is no duration to show for a call nobody took");
        });
    }

    [Test]
    public async Task CallOutsideAnyConversation_LeavesNoEntry()
    {
        await HandleAsync(new CallEnded { CallId = CallId, Reason = CallEndReason.UserEnded });

        Assert.That(HistoryEntry(), Is.Null, "there is no conversation to write it into");
    }

    // ══════════════════════════════════════════════════════════════════════════ Notification
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Ending_TellsTheWholeConversation()
    {
        await HandleAsync(Event());

        var send = HubClients.Sends.Single(s => s.Method == "conversation.CallStateChanged");
        Assert.That(send.Target, Does.Contain(Bystander),
            "the affordance is shown to members who were never on the call, so they are exactly "
            + "the people who need telling it is gone");
    }

    [Test]
    public async Task CallOutsideAnyConversation_TellsNobody()
    {
        await HandleAsync(new CallEnded { CallId = CallId, Reason = CallEndReason.UserEnded });

        Assert.That(HubClients.SentMessages.Any(m => m.Method == "conversation.CallStateChanged"), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════ Cleanup
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Ending_ClearsTheConversationCallIndex()
    {
        _cache.SetEntry(CallService.ConversationCallKey(ConversationId), CallId);

        await HandleAsync(Event());

        Assert.That(_cache.HasEntry(CallService.ConversationCallKey(ConversationId)), Is.False,
            "left behind, it would advertise a dead call to every member who opened the conversation");
    }

    [Test]
    public async Task Ending_DropsTheCallsStreamViewers()
    {
        await _viewers.WatchAsync(StreamViewerStore.CallScope(CallId), "share-1", Callee);

        await HandleAsync(Event());

        Assert.That(await _viewers.SnapshotAsync(StreamViewerStore.CallScope(CallId)), Is.Empty);
    }

    [Test]
    public async Task Ending_EvictsTheCall()
    {
        _cache.SetEntry(CallEntity.GetCacheId(CallId), "{}");

        await HandleAsync(Event());

        Assert.That(_cache.HasEntry(CallEntity.GetCacheId(CallId)), Is.False);
    }
}
