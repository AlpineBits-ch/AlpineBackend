using Federation.Application.Bus.Inbound;
using Federation.Application.Dtos.Events.Bidirectional.Conversation;
using Federation.Application.Dtos.Events.Bidirectional.Guild;
using Federation.Application.Dtos.Events.Bidirectional.Messaging;
using Federation.Application.Dtos.Events.Bidirectional.Social;
using Federation.Contracts.Materialization.Conversation;
using Federation.Contracts.Materialization.Guild;
using Federation.Contracts.Materialization.Messaging;
using Federation.Contracts.Materialization.Social;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Federation.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Federation.Tests;

[TestFixture]
public class InboundEventDispatcherTests
{
    private MicroserviceContext _db = null!;
    private FakeMessageBus _bus = null!;
    private FederationInstance _origin = null!;

    [SetUp]
    public async Task SetUp()
    {
        var opts = new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase($"dispatch-{Guid.NewGuid()}")
            .Options;
        _db = new MicroserviceContext(opts);
        _bus = new FakeMessageBus();

        _origin = new FederationInstance { Id = "fein_origin", Host = "https://origin.example.com", Name = "Origin", Status = FederationStatus.Active };
        _db.FederationInstances.Add(_origin);
        await _db.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task DispatchAsync_UnknownOriginHost_PublishesNothing()
    {
        var @event = new MessageCreated { Host = "https://unregistered.example.com", ChannelId = "ch_1", MessageId = "m1" };

        await InboundEventDispatcher.DispatchAsync(@event, _db, _bus, default);

        Assert.That(_bus.Published, Is.Empty);
    }

    [Test]
    public async Task DispatchAsync_MessageCreated_PublishesFederatedMessageCreatedReceived_WithStrippedChannelId()
    {
        var @event = new MessageCreated
        {
            Host = _origin.Host, EventId = "evt_1", SenderId = "usr_1:origin.example.com",
            ChannelId = "ch_1:home.example.com", MessageId = "m1", Content = "hi"u8.ToArray(),
        };

        await InboundEventDispatcher.DispatchAsync(@event, _db, _bus, default);

        Assert.That(_bus.Published, Has.Count.EqualTo(1));
        Assert.That(_bus.Published[0], Is.InstanceOf<FederatedMessageCreatedReceived>());
        var command = (FederatedMessageCreatedReceived)_bus.Published[0];
        Assert.That(command.ChannelId, Is.EqualTo("ch_1"), "Inbound channel id must have the :home.example.com suffix stripped");
        Assert.That(command.OriginInstanceId, Is.EqualTo("fein_origin"));
        Assert.That(command.SenderId, Is.EqualTo("usr_1:origin.example.com"));
        Assert.That(command.MessageId, Is.EqualTo("m1"));
    }

    [Test]
    public async Task DispatchAsync_GuildMemberBanned_StripsGuildAndUserIds()
    {
        var @event = new GuildMemberBanned
        {
            Host = _origin.Host, EventId = "evt_2", SenderId = "usr_mod:origin.example.com",
            GuildId = "gld_1:home.example.com", BannedUserId = "usr_bad:home.example.com", Reason = "spam",
        };

        await InboundEventDispatcher.DispatchAsync(@event, _db, _bus, default);

        var command = (FederatedGuildMemberBannedReceived)_bus.Published.Single();
        Assert.That(command.GuildId, Is.EqualTo("gld_1"));
        Assert.That(command.BannedUserId, Is.EqualTo("usr_bad"));
        Assert.That(command.Reason, Is.EqualTo("spam"));
    }

    [Test]
    public async Task DispatchAsync_GuildMemberJoined_UserIdComesFromSender()
    {
        // The joining user is a user of the origin instance, so their federated id is qualified
        // with the origin host - matching the convention every other event here uses.
        var @event = new GuildMemberJoined
        {
            Host = _origin.Host, EventId = "evt_3", SenderId = "usr_joiner:origin.example.com",
            GuildId = "gld_1:home.example.com",
        };

        await InboundEventDispatcher.DispatchAsync(@event, _db, _bus, default);

        var command = (FederatedGuildMemberJoinedReceived)_bus.Published.Single();
        Assert.That(command.UserId, Is.EqualTo("usr_joiner"));
        Assert.That(command.GuildId, Is.EqualTo("gld_1"));
    }

    [Test]
    public async Task DispatchAsync_SenderFromDifferentInstanceThanSigner_PublishesNothing()
    {
        // The envelope signature only proves which instance sent the event, not whose id the
        // payload names.
        var @event = new SocialFriendRemoved
        {
            Host = _origin.Host, EventId = "evt_spoof",
            SenderId = "usr_victim:other-instance.example.com",
            TargetUserId = "usr_local:home.example.com",
        };

        await InboundEventDispatcher.DispatchAsync(@event, _db, _bus, default);

        Assert.That(_bus.Published, Is.Empty);
    }

    [Test]
    public async Task DispatchAsync_UnqualifiedSenderId_PublishesNothing()
    {
        // An unqualified sender is indistinguishable from a local user id, so it fails closed.
        var @event = new SocialFriendRemoved
        {
            Host = _origin.Host, EventId = "evt_bare",
            SenderId = "usr_local", TargetUserId = "usr_other:home.example.com",
        };

        await InboundEventDispatcher.DispatchAsync(@event, _db, _bus, default);

        Assert.That(_bus.Published, Is.Empty);
    }

    [Test]
    public async Task DispatchAsync_SocialFriendRequest_StripsTargetUserId()
    {
        var @event = new SocialFriendRequest
        {
            Host = _origin.Host, EventId = "evt_4", SenderId = "usr_1:origin.example.com",
            TargetUserId = "usr_2:home.example.com",
        };

        await InboundEventDispatcher.DispatchAsync(@event, _db, _bus, default);

        var command = (FederatedFriendRequestReceived)_bus.Published.Single();
        Assert.That(command.TargetUserId, Is.EqualTo("usr_2"));
    }

    [Test]
    public async Task DispatchAsync_ConversationCreated_StripsAllMemberIds()
    {
        var @event = new ConversationCreated
        {
            Host = _origin.Host, EventId = "evt_5", SenderId = "usr_1:origin.example.com",
            ConversationId = "conv_1:home.example.com",
            MemberIds = ["usr_a:home.example.com", "usr_b:origin.example.com"],
        };

        await InboundEventDispatcher.DispatchAsync(@event, _db, _bus, default);

        var command = (FederatedConversationCreatedReceived)_bus.Published.Single();
        Assert.That(command.ConversationId, Is.EqualTo("conv_1"));
        Assert.That(command.MemberIds, Is.EqualTo(new[] { "usr_a", "usr_b" }));
    }

    [Test]
    public async Task DispatchAsync_GuildInviteAccepted_OutboundOnlyEventType_PublishesNothing()
    {
        // GuildInviteAccepted/Revoked are outbound-only per the venta/v0.1 catalogue - a remote
        // instance should never send us one, but if it somehow does, dispatch is a safe no-op.
        var @event = new GuildInviteAccepted { Host = _origin.Host, EventId = "evt_6", GuildId = "gld_1" };

        await InboundEventDispatcher.DispatchAsync(@event, _db, _bus, default);

        Assert.That(_bus.Published, Is.Empty);
    }
}
