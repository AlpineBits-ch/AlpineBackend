using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Drafts: one row per author per context, last write wins, and private to whoever typed it.
/// </summary>
[TestFixture]
public class MessageDraftEndpointsTests
{
    private const string ChannelId = "chan-1";
    private const string ConversationId = "conv-1";
    private const string UserId = "user-1";
    private const string OtherUserId = "user-2";

    private TestMessagingContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private ConversationPermissionService _permissions = null!;
    private FakeMessagingHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissions = new ConversationPermissionService(_context, _cache);
        _hub = new FakeMessagingHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════════ upsert

    [Test]
    public async Task A_first_save_stores_the_body_and_the_reply_target()
    {
        var result = await UpsertAsync(ChannelId, "the first half of a long scene", inReplyTo: "mesg_1");

        var stored = await _context.MessageDrafts.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<MessageDraftDto>>());
            Assert.That(stored.Content, Is.EqualTo("the first half of a long scene"));
            Assert.That(stored.InReplyTo, Is.EqualTo("mesg_1"),
                "losing the reply target is half of what a lost draft costs");
            Assert.That(stored.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(stored.ConversationId, Is.Null);
        });
    }

    /// <summary>The point of the storage rule: a draft is state, not a log.</summary>
    [Test]
    public async Task A_second_save_overwrites_rather_than_appending()
    {
        await UpsertAsync(ChannelId, "first");
        await UpsertAsync(ChannelId, "second");

        var stored = await _context.MessageDrafts.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored, Has.Count.EqualTo(1), "one row per author per context, always");
            Assert.That(stored[0].Content, Is.EqualTo("second"));
        });
    }

    [Test]
    public async Task A_conversation_draft_is_stored_against_the_conversation()
    {
        await SeedConversationAsync();

        await UpsertAsync(ConversationId, "a private thought");

        var stored = await _context.MessageDrafts.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored.ConversationId, Is.EqualTo(ConversationId));
            Assert.That(stored.ChannelId, Is.Null,
                "a conversation id must not be recorded as a channel id");
        });
    }

    [Test]
    public async Task Saving_an_empty_body_discards_the_draft()
    {
        await UpsertAsync(ChannelId, "half a paragraph");

        var result = await UpsertAsync(ChannelId, string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(_context.MessageDrafts.Any(), Is.False, "clearing the composer clears the draft");
        });
    }

    [Test]
    public async Task A_body_over_the_hard_ceiling_is_refused()
    {
        var result = await UpsertAsync(
            ChannelId, new string('a', MessageLengthPolicy.HardCeilingCharacters + 1));

        Assert.Multiple(() =>
        {
            Assert.That(((IStatusCodeHttpResult)result).StatusCode,
                Is.EqualTo(MessageLengthPolicy.TooLongStatusCode));
            Assert.That(_context.MessageDrafts.Any(), Is.False);
        });
    }

    [Test]
    public async Task A_body_at_the_hard_ceiling_is_stored()
    {
        var result = await UpsertAsync(ChannelId, new string('a', MessageLengthPolicy.HardCeilingCharacters));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<MessageDraftDto>>());
            Assert.That(_context.MessageDrafts.Any(), Is.True);
        });
    }

    [Test]
    public async Task Somebody_who_may_not_write_in_the_channel_may_not_store_a_draft_there()
    {
        var result = await UpsertAsync(ChannelId, "hello", allowed: false);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(_context.MessageDrafts.Any(), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ read

    [Test]
    public async Task A_context_with_nothing_typed_in_it_answers_not_found()
    {
        var result = await MessageDraftEndpoints.GetDraft(ChannelId, _context, TestPrincipal.ForUser(UserId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>
    /// The whole privacy rule in one test: a draft belongs to its author and to nobody else, however
    /// many people can see the channel it was typed into.
    /// </summary>
    [Test]
    public async Task Another_member_of_the_same_channel_cannot_read_it()
    {
        await UpsertAsync(ChannelId, "my half-written scene");

        var mine = await MessageDraftEndpoints.GetDraft(ChannelId, _context, TestPrincipal.ForUser(UserId));
        var theirs = await MessageDraftEndpoints.GetDraft(ChannelId, _context, TestPrincipal.ForUser(OtherUserId));

        Assert.Multiple(() =>
        {
            Assert.That(mine, Is.InstanceOf<Ok<MessageDraftDto>>());
            Assert.That(theirs, Is.InstanceOf<NotFound>());
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ delete

    [Test]
    public async Task Deleting_removes_the_row()
    {
        await UpsertAsync(ChannelId, "never mind");

        var result = await MessageDraftEndpoints.DeleteDraft(
            ChannelId, "device-a", _context, TestPrincipal.ForUser(UserId), _hub);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(_context.MessageDrafts.Any(), Is.False);
        });
    }

    [Test]
    public async Task Deleting_somebody_elses_draft_leaves_it_alone()
    {
        await UpsertAsync(ChannelId, "my half-written scene");

        await MessageDraftEndpoints.DeleteDraft(
            ChannelId, "device-a", _context, TestPrincipal.ForUser(OtherUserId), _hub);
        await _context.SaveChangesAsync();

        Assert.That(_context.MessageDrafts.Any(d => d.UserId == UserId), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════ realtime

    /// <summary>
    /// No channel fan-out, no push, no unread effect: the only realtime a draft produces goes back
    /// to the person who typed it, for their other devices.
    /// </summary>
    [Test]
    public async Task The_only_realtime_goes_to_the_authors_own_connections()
    {
        await UpsertAsync(ChannelId, "typing");

        Assert.Multiple(() =>
        {
            Assert.That(Sends.Select(s => s.Target), Is.All.EqualTo("user:" + UserId));
            Assert.That(Sends.Select(s => s.Method),
                Is.All.EqualTo(MessageDraftEndpoints.DraftUpdatedEvent));
        });
    }

    /// <summary>
    /// Without the writing device on the event, every device applies its own echo over text the user
    /// is still typing.
    /// </summary>
    [Test]
    public async Task The_update_event_names_the_context_and_the_device_that_wrote_it()
    {
        await UpsertAsync(ChannelId, "typing", deviceId: "device-a");

        var payload = (MessageDraftDto)Sends.Single().Args[0];

        Assert.Multiple(() =>
        {
            Assert.That(payload.ContextId, Is.EqualTo(ChannelId));
            Assert.That(payload.DeviceId, Is.EqualTo("device-a"));
        });
    }

    [Test]
    public async Task The_delete_event_names_the_context_and_the_device_too()
    {
        await MessageDraftEndpoints.DeleteDraft(
            ChannelId, "device-a", _context, TestPrincipal.ForUser(UserId), _hub);

        var send = Sends.Single();
        var payload = send.Args[0];

        Assert.Multiple(() =>
        {
            Assert.That(send.Method, Is.EqualTo(MessageDraftEndpoints.DraftDeletedEvent));
            Assert.That(Read(payload, "contextId"), Is.EqualTo(ChannelId));
            Assert.That(Read(payload, "deviceId"), Is.EqualTo("device-a"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ helpers

    /// <summary>Every realtime send this fixture produced, with its target.</summary>
    private List<(string Target, string Method, object[] Args)> Sends =>
        ((FakeHubClients)_hub.Clients).Sends;

    private static object? Read(object body, string property) =>
        body.GetType().GetProperty(property)!.GetValue(body);

    private async Task<IResult> UpsertAsync(
        string contextId, string content, string? inReplyTo = null, string? deviceId = null,
        bool allowed = true, string userId = UserId)
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
            {
                IsAllowed = allowed, Permission = r.Permission,
            },
            _ => throw new InvalidOperationException("unexpected " + msg.GetType().Name),
        });

        var result = await MessageDraftEndpoints.UpsertDraft(
            contextId,
            new UpsertMessageDraftDto { Content = content, InReplyTo = inReplyTo, SenderDeviceId = deviceId },
            _context, TestPrincipal.ForUser(userId), bus, _permissions, _hub);

        // The Wolverine transactional middleware commits this in production; a direct call has to.
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return result;
    }

    private async Task SeedConversationAsync()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = ConversationId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Members.Add(new ConversationMember
        {
            Id = "cmem-1",
            UserId = UserId,
            ConversationId = ConversationId,
            PublicKey = [],
            CachedUserName = "test-user",
            CachedUserHash = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
