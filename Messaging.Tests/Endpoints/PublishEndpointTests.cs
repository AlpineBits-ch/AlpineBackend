using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Endpoints;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence.Repositories;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Covers PublishEndpoint - the Announcement channel cross-posting endpoint: not-found/validation
/// paths, the PinMessages permission gate reuse, fan-out to every follower channel via
/// CreateMessageCommand, and mention-stripping on the crossposted copies.
/// </summary>
[TestFixture]
public class PublishEndpointTests
{
    private TestMessagingContext _context = null!;
    private EfCoreMessageRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _repo = new EfCoreMessageRepository(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Message MakeChannelMessage(string channelId = "announce-chan") => Message.Create(new CreateMessageParams
    {
        Content = "breaking news"u8.ToArray(),
        ChannelId = channelId,
        AuthorId = "author-1",
        Mentions = ["user-x"],
        RoleMentions = ["role-x"],
        MentionsEveryone = true,
    });

    [Test]
    public async Task Publish_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new PublishEndpoint();
        var bus = new FakeMessageBus();

        var result = await endpoint.Publish("msg-1", _repo, TestPrincipal.Anonymous(), bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Publish_MessageNotFound_ReturnsNotFound()
    {
        var endpoint = new PublishEndpoint();
        var bus = new FakeMessageBus();

        var result = await endpoint.Publish("nope", _repo, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Publish_ConversationMessage_ReturnsBadRequest()
    {
        var message = Message.Create(new CreateMessageParams { Content = "hi"u8.ToArray(), ConversationId = "conv-1", AuthorId = "author-1" });
        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new PublishEndpoint();
        var bus = new FakeMessageBus();

        var result = await endpoint.Publish(message.Id, _repo, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Publish_UserLacksPinMessagesPermission_ReturnsForbid()
    {
        var message = MakeChannelMessage();
        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new PublishEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = r.Permission },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.Publish(message.Id, _repo, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Publish_PermissionRequestUsesPinMessagesPermission()
    {
        var message = MakeChannelMessage();
        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new PublishEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest => new HasUserPermissionToChannelResponse { IsAllowed = false, Permission = ExternalPermission.PinMessages },
            _ => throw new InvalidOperationException("unexpected"),
        });

        await endpoint.Publish(message.Id, _repo, TestPrincipal.ForUser("user-1"), bus);

        var permissionRequest = (HasUserPermissionToChannelRequest)bus.Invoked.Single(m => m is HasUserPermissionToChannelRequest);
        Assert.That(permissionRequest.Permission, Is.EqualTo(ExternalPermission.PinMessages));
    }

    [Test]
    public async Task Publish_NotAnAnnouncementChannel_ReturnsBadRequest()
    {
        var message = MakeChannelMessage();
        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new PublishEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            GetChannelFollowersRequest => new GetChannelFollowersResponse { IsAnnouncementChannel = false },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.Publish(message.Id, _repo, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Publish_NoFollowers_ReturnsOkWithZeroPublished()
    {
        var message = MakeChannelMessage();
        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new PublishEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            GetChannelFollowersRequest => new GetChannelFollowersResponse { IsAnnouncementChannel = true, TargetChannelIds = [] },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.Publish(message.Id, _repo, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(200));
        Assert.That(bus.Invoked.Any(m => m is CreateMessageCommand), Is.False, "No CreateMessageCommand should be dispatched when there are no followers");
    }

    [Test]
    public async Task Publish_WithFollowers_FansOutCreateMessageCommandPerTargetChannel_AndStripsMentions()
    {
        var message = MakeChannelMessage();
        await _repo.CreateMessageAsync(message);
        await _context.SaveChangesAsync();

        var endpoint = new PublishEndpoint();
        var bus = new FakeMessageBus(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse { IsAllowed = true, Permission = r.Permission },
            GetChannelFollowersRequest => new GetChannelFollowersResponse { IsAnnouncementChannel = true, TargetChannelIds = ["follower-1", "follower-2"] },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.Publish(message.Id, _repo, TestPrincipal.ForUser("user-1"), bus);

        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(200));

        var dispatched = bus.Invoked.OfType<CreateMessageCommand>().ToList();
        Assert.That(dispatched, Has.Count.EqualTo(2));
        Assert.That(dispatched.Select(c => c.ChannelId), Is.EquivalentTo(new[] { "follower-1", "follower-2" }));
        Assert.Multiple(() =>
        {
            foreach (var command in dispatched)
            {
                Assert.That(command.Mentions, Is.Empty, "Mentions from the source guild are meaningless in the target guild");
                Assert.That(command.RoleMentions, Is.Empty);
                Assert.That(command.MentionsEveryone, Is.False);
                Assert.That(command.MentionsHere, Is.False);
                Assert.That(command.AuthorId, Is.EqualTo("author-1"));
                Assert.That(command.EncryptionState, Is.EqualTo(global::Messaging.Contracts.Bus.Commands.MessageEncryptionState.Plain));
            }
        });
    }
}
