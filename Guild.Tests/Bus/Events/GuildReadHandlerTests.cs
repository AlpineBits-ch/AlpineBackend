using Echo.Realtime;
using Guild.Application.Bus.Events.Realtime;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Messaging.Contracts.Bus.Request;
using Messaging.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus.Events;

/// <summary>
/// Covers <see cref="GuildReadHandler"/>, the read-cursor ack.
///
/// The interesting part is <c>LastReadAt</c>. The command carries only a message id, but the unread
/// predicate compares timestamps - ids minted before the ULID change do not sort, so comparing
/// <c>Channel.LastMessageId</c> against <c>LastReadMessageId</c> is wrong for any pre-existing
/// account and always will be. So the handler has to turn the acked id into an instant, and the
/// tests below pin down all three ways it can: free from the channel row (the common case), one bus
/// round trip (acked while scrolled up), and the fallbacks when neither works.
/// </summary>
[TestFixture]
public class GuildReadHandlerTests
{
    private const string GuildId = "gild-1";
    private const string ChannelId = "chan-1";
    private const string UserId = "user-1";
    private const string MemberId = "memb-1";

    private static readonly DateTimeOffset HeadCreatedAt = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OlderCreatedAt = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private TestGuildContext _context = null!;
    private GuildReadHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _handler = new GuildReadHandler();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedAsync(string? lastMessageId = "mesg-head", int messageCount = 10)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = "user-owner", CreatedAt = now, UpdatedAt = now,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "chat", Description = "d", Type = ChannelType.Text,
            LastMessageId = lastMessageId,
            LastActivityAt = lastMessageId is null ? null : HeadCreatedAt,
            MessageCount = messageCount,
            CreatedAt = now, UpdatedAt = now,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            SearchValue = "USER-1", CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
    }

    /// <summary>A bus that answers GetMessageRequest with a message at <see cref="OlderCreatedAt"/>,
    /// or with "no such message" when <paramref name="found"/> is false.</summary>
    private static FakeInvokingMessageBus MessageBus(bool found = true)
    {
        var bus = new FakeInvokingMessageBus();
        bus.SetResponse<GetMessageRequest>(new GetMessageResponse
        {
            Message = found ? new MessageSummary { Id = "mesg-older", CreatedAt = OlderCreatedAt, AuthorId = "user-2" } : null,
        });
        return bus;
    }

    /// <summary>Answers nothing, so any InvokeAsync throws - which is how the tests below prove a
    /// path did not need the bus at all.</summary>
    private static FakeInvokingMessageBus ThrowingBus() => new();

    private Task AckAsync(string messageId, FakeInvokingMessageBus? bus = null) =>
        _handler.Handle(
            new UpdateGuildReadCommand(UserId, ChannelId, messageId),
            _context,
            bus ?? MessageBus(),
            NullLogger<GuildReadHandler>.Instance);

    private ReadState? Stored() =>
        _context.ReadStates.AsNoTracking().FirstOrDefault(r => r.ChannelId == ChannelId && r.MemberId == MemberId);

    // ══════════════════════════════════════════════════════════════════════
    // The common case: acking the head costs nothing extra
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AckingTheChannelHead_TakesTheTimestampFromTheChannelRow()
    {
        await SeedAsync();
        var bus = ThrowingBus();

        // Deliberately a bus that would explode if touched: acking the head must not need it.
        await AckAsync("mesg-head", bus);

        Assert.Multiple(() =>
        {
            Assert.That(Stored()!.LastReadAt, Is.EqualTo(HeadCreatedAt));
            Assert.That(bus.Invoked, Is.Empty, "the channel row already knows when its head landed");
        });
    }

    [Test]
    public async Task AckingTheHead_SnapshotsTheMessageCount()
    {
        await SeedAsync(messageCount: 42);
        _context.ReadStates.Add(new ReadState
        {
            Id = "reta-1", ChannelId = ChannelId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        await AckAsync("mesg-head", ThrowingBus());

        var stored = Stored()!;
        Assert.Multiple(() =>
        {
            Assert.That(stored.MessageCountAtRead, Is.EqualTo(42));
            Assert.That(stored.LastReadMessageId, Is.EqualTo("mesg-head"));
            // Mentions are not cleared here any more: they are counted from the index and the
            // broadcast rows against LastReadAt, so moving the cursor clears them by construction.
        });
    }

    [Test]
    public async Task FirstAck_CreatesTheReadState()
    {
        await SeedAsync();

        await AckAsync("mesg-head", ThrowingBus());

        Assert.That(Stored(), Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Acking while scrolled up
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AckingAnOlderMessage_ResolvesItsRealTimestampOverTheBus()
    {
        await SeedAsync();
        var bus = MessageBus();

        await AckAsync("mesg-older", bus);

        Assert.Multiple(() =>
        {
            Assert.That(Stored()!.LastReadAt, Is.EqualTo(OlderCreatedAt));
            Assert.That(bus.Invoked.OfType<GetMessageRequest>().Single().MessageId, Is.EqualTo("mesg-older"));
        });
    }

    /// <summary>Marking read further back than the head must leave the channel unread, which is the
    /// whole reason the exact timestamp is worth a round trip.</summary>
    [Test]
    public async Task AckingAnOlderMessage_LeavesTheChannelStillUnread()
    {
        await SeedAsync();

        await AckAsync("mesg-older");

        Assert.That(Stored()!.LastReadAt, Is.LessThan(HeadCreatedAt));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Failing gracefully
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Acking a message that has since been deleted is ordinary, not exceptional - it was
    /// rendered, then removed, then acked. The channel head is the honest answer.</summary>
    [Test]
    public async Task AckingAMessageThatNoLongerExists_FallsBackToTheChannelHead()
    {
        await SeedAsync();

        await AckAsync("mesg-deleted", MessageBus(found: false));

        Assert.That(Stored()!.LastReadAt, Is.EqualTo(HeadCreatedAt));
    }

    /// <summary>Over-marking beats under-marking here: the member loses an unread badge they might
    /// have wanted, rather than being left with a channel that will not stop shouting at them.</summary>
    [Test]
    public async Task MessagingUnreachable_FallsBackToNowRatherThanThrowing()
    {
        await SeedAsync();
        var before = DateTimeOffset.UtcNow;

        Assert.DoesNotThrowAsync(() => AckAsync("mesg-older", ThrowingBus()));

        Assert.That(Stored()!.LastReadAt, Is.GreaterThanOrEqualTo(before));
    }

    [Test]
    public async Task ChannelWithNoMessagesYet_AcksWithoutThrowing()
    {
        await SeedAsync(lastMessageId: null, messageCount: 0);

        Assert.DoesNotThrowAsync(() => AckAsync("mesg-phantom", MessageBus(found: false)));

        Assert.That(Stored(), Is.Not.Null);
    }

    [Test]
    public async Task UnknownChannel_DoesNothing()
    {
        await SeedAsync();

        await _handler.Handle(
            new UpdateGuildReadCommand(UserId, "chan-nope", "mesg-head"),
            _context, ThrowingBus(), NullLogger<GuildReadHandler>.Instance);

        Assert.That(_context.ReadStates.AsNoTracking().ToList(), Is.Empty);
    }

    [Test]
    public async Task UserWhoIsNotAMember_DoesNothing()
    {
        await SeedAsync();

        await _handler.Handle(
            new UpdateGuildReadCommand("user-stranger", ChannelId, "mesg-head"),
            _context, ThrowingBus(), NullLogger<GuildReadHandler>.Instance);

        Assert.That(_context.ReadStates.AsNoTracking().ToList(), Is.Empty);
    }

    [Test]
    public async Task AckingTwice_IsIdempotent()
    {
        await SeedAsync();

        await AckAsync("mesg-head", ThrowingBus());
        await AckAsync("mesg-head", ThrowingBus());

        var states = _context.ReadStates.AsNoTracking().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(states, Has.Count.EqualTo(1));
            Assert.That(states[0].LastReadAt, Is.EqualTo(HeadCreatedAt));
        });
    }
}
