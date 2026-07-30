using Echo.Realtime;
using Messaging.Application.Handler.Realtime;
using Messaging.Tests.Helpers;
using Social.Contracts.Bus.Integration.Events;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Messaging.Tests.Handlers.Realtime;

/// <summary>Covers UserConnectedHandler: publishes a presence UserActiveEvent and pushes
/// "presence.UserOnline" to every relationship the connecting user has, regardless of
/// RelationshipStatus (the handler doesn't filter - it fans out to all rows).</summary>
[TestFixture]
public class UserConnectedHandlerTests
{
    private FakeMessagingHubContext _hub = null!;

    [SetUp]
    public void SetUp() => _hub = new FakeMessagingHubContext();

    private static GetProfileByUserIdResponse ProfileWithRelationships(params string[] relationshipUserIds) => new()
    {
        Profile = new ProfileDto
        {
            Id = "profile-1",
            UserName = "tester",
            Hash = 1234,
            Font = "Default",
            AvatarUrl = "",
            BannerUrl = "",
            Relationships = relationshipUserIds.Select(id => new RelationshipDto
            {
                Id = "rel-" + id,
                UserId = id,
                Status = RelationshipStatus.Accepted,
            }).ToList(),
        },
    };

    [Test]
    public async Task Handle_PublishesUserActiveEvent()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest => ProfileWithRelationships(),
            _ => throw new InvalidOperationException("unexpected"),
        });

        await UserConnectedHandler.Handle(new UserConnected("user-1"), bus, _hub);

        Assert.That(bus.Published.Any(p => p is UserActiveEvent evt && evt.UserId == "user-1"), Is.True);
    }

    [Test]
    public async Task Handle_NotifiesEachRelationship_OverHub()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest => ProfileWithRelationships("friend-1", "friend-2"),
            _ => throw new InvalidOperationException("unexpected"),
        });

        await UserConnectedHandler.Handle(new UserConnected("user-1"), bus, _hub);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Has.Count.EqualTo(2));
        Assert.That(hubClients.SentMessages.All(m => m.Method == "presence.UserOnline"), Is.True);
    }

    [Test]
    public async Task Handle_ProfileHasNoRelationships_NoHubNotificationsSent()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest => ProfileWithRelationships(),
            _ => throw new InvalidOperationException("unexpected"),
        });

        await UserConnectedHandler.Handle(new UserConnected("user-1"), bus, _hub);

        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages, Is.Empty);
    }

    [Test]
    public async Task Handle_ProfileIsNull_DoesNotThrow()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest => new GetProfileByUserIdResponse { Profile = null },
            _ => throw new InvalidOperationException("unexpected"),
        });

        Assert.DoesNotThrowAsync(() => UserConnectedHandler.Handle(new UserConnected("user-1"), bus, _hub));
    }
}
