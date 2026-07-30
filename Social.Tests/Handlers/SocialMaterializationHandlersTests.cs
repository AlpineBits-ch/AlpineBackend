using Federation.Contracts.Materialization.Social;
using Social.Api.Bus.Federation;
using Social.Api.Dtos.Realtime;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Handlers;

[TestFixture]
public class SocialMaterializationHandlersTests
{
    private string _dbName = null!;
    private TestSocialContext _context = null!;
    private FakeSocialHubContext _hub = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _context = new TestSocialContext(_dbName);
        _hub = new FakeSocialHubContext();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── FederatedFriendRequestReceived ──────────────────────────────────────

    [Test]
    public async Task RequestReceived_NoLocalProfile_CreatesShadowProfileButNoRelationship()
    {
        // The shadow profile for the remote sender is created/persisted before the local target
        // is even looked up (see GetOrCreateShadowProfileAsync call order in the handler), so it
        // exists regardless of whether the local target profile does - only relationship creation
        // is actually gated on the local profile existing.
        await SocialMaterializationHandlers.Handle(new FederatedFriendRequestReceived
        {
            EventId = "evt-1",
            OriginInstanceId = "instance-a",
            SenderId = "remote-user",
            TargetUserId = "local-user-does-not-exist",
        }, _context, _hub, CancellationToken.None);

        Assert.That(_context.Profiles.Single().UserId, Is.EqualTo("remote-user"));
        Assert.That(_context.Relationships.Any(), Is.False);
    }

    [Test]
    public async Task RequestReceived_LocalProfileExists_CreatesShadowProfileAndIncomingRelationship()
    {
        var localProfile = Profile.Create(new CreateProfileParams { UserId = "local-user", Username = "local" });
        _context.Profiles.Add(localProfile);
        await _context.SaveChangesAsync();

        await SocialMaterializationHandlers.Handle(new FederatedFriendRequestReceived
        {
            EventId = "evt-1",
            OriginInstanceId = "instance-a",
            SenderId = "remote-user",
            TargetUserId = "local-user",
        }, _context, _hub, CancellationToken.None);

        var shadowProfile = _context.Profiles.Single(p => p.UserId == "remote-user");
        Assert.Multiple(() =>
        {
            Assert.That(shadowProfile.FederatedServerId, Is.EqualTo("instance-a"));
            Assert.That(shadowProfile.UserName, Is.EqualTo("remote-user"));
        });

        var relationship = _context.Relationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(relationship.OwnerId, Is.EqualTo(localProfile.Id), "only the local (incoming) side is persisted");
            Assert.That(relationship.TargetId, Is.EqualTo(shadowProfile.Id));
            Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.PendingIncoming));
        });

        // A federated request used to land in the database completely silently - the local user
        // only found out by polling GET /api/v1/relationships.
        var push = _hub.To("local-user").Single();
        Assert.That(push.Method, Is.EqualTo("social.FriendRequestCreated"));
        var payload = push.Payload<FriendRelationshipPayload>();
        Assert.Multiple(() =>
        {
            Assert.That(payload.RelationshipId, Is.EqualTo(relationship.Id));
            Assert.That(payload.Status, Is.EqualTo(RelationshipStatus.PendingIncoming));
            Assert.That(payload.UserId, Is.EqualTo("remote-user"));
        });
    }

    [Test]
    public async Task RequestReceived_ShadowProfileAlreadyExists_ReusesIt()
    {
        var localProfile = Profile.Create(new CreateProfileParams { UserId = "local-user", Username = "local" });
        var existingShadow = Profile.Create(new CreateProfileParams { UserId = "remote-user", Username = "remote-user" });
        existingShadow.FederatedServerId = "instance-a";
        _context.Profiles.AddRange(localProfile, existingShadow);
        await _context.SaveChangesAsync();

        await SocialMaterializationHandlers.Handle(new FederatedFriendRequestReceived
        {
            EventId = "evt-1",
            OriginInstanceId = "instance-a",
            SenderId = "remote-user",
            TargetUserId = "local-user",
        }, _context, _hub, CancellationToken.None);

        Assert.That(_context.Profiles.Count(p => p.UserId == "remote-user"), Is.EqualTo(1), "must not create a duplicate shadow profile");
    }

    [Test]
    public async Task RequestReceived_RelationshipAlreadyExists_DoesNotCreateDuplicate()
    {
        var localProfile = Profile.Create(new CreateProfileParams { UserId = "local-user", Username = "local" });
        var shadow = Profile.Create(new CreateProfileParams { UserId = "remote-user", Username = "remote-user" });
        _context.Profiles.AddRange(localProfile, shadow);
        await _context.SaveChangesAsync();

        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_existing",
            OwnerId = localProfile.Id,
            TargetId = shadow.Id,
            Status = RelationshipStatus.PendingIncoming,
        });
        await _context.SaveChangesAsync();

        await SocialMaterializationHandlers.Handle(new FederatedFriendRequestReceived
        {
            EventId = "evt-2",
            OriginInstanceId = "instance-a",
            SenderId = "remote-user",
            TargetUserId = "local-user",
        }, _context, _hub, CancellationToken.None);

        Assert.That(_context.Relationships.Count(), Is.EqualTo(1));
    }

    // ── FederatedFriendAcceptedReceived / Rejected / Removed ────────────────

    [Test]
    public async Task AcceptedReceived_ExistingRelationship_SetsFriends()
    {
        var localProfile = Profile.Create(new CreateProfileParams { UserId = "local-user", Username = "local" });
        var shadow = Profile.Create(new CreateProfileParams { UserId = "remote-user", Username = "remote-user" });
        _context.Profiles.AddRange(localProfile, shadow);
        await _context.SaveChangesAsync();

        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_1",
            OwnerId = localProfile.Id,
            TargetId = shadow.Id,
            Status = RelationshipStatus.PendingOutgoing,
        });
        await _context.SaveChangesAsync();

        await SocialMaterializationHandlers.Handle(new FederatedFriendAcceptedReceived
        {
            EventId = "evt-3",
            OriginInstanceId = "instance-a",
            SenderId = "remote-user",
            InitiatorUserId = "local-user",
        }, _context, _hub, CancellationToken.None);

        var relationship = _context.Relationships.Single(r => r.Id == "rlsp_1");
        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.Friends));

        var push = _hub.To("local-user").Single();
        Assert.Multiple(() =>
        {
            Assert.That(push.Method, Is.EqualTo("social.FriendRequestAccepted"));
            Assert.That(push.Payload<FriendRelationshipPayload>().Status, Is.EqualTo(RelationshipStatus.Friends));
        });
    }

    [Test]
    public async Task AcceptedReceived_Redelivered_DoesNotPushTwice()
    {
        // Federation delivery is at-least-once; a redelivered accept finds the row already at
        // Friends and must not push social.FriendRequestAccepted a second time.
        var localProfile = Profile.Create(new CreateProfileParams { UserId = "local-user", Username = "local" });
        var shadow = Profile.Create(new CreateProfileParams { UserId = "remote-user", Username = "remote-user" });
        _context.Profiles.AddRange(localProfile, shadow);
        await _context.SaveChangesAsync();

        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_dup",
            OwnerId = localProfile.Id,
            TargetId = shadow.Id,
            Status = RelationshipStatus.PendingOutgoing,
        });
        await _context.SaveChangesAsync();

        var message = new FederatedFriendAcceptedReceived
        {
            EventId = "evt-dup",
            OriginInstanceId = "instance-a",
            SenderId = "remote-user",
            InitiatorUserId = "local-user",
        };

        await SocialMaterializationHandlers.Handle(message, _context, _hub, CancellationToken.None);
        await SocialMaterializationHandlers.Handle(message, _context, _hub, CancellationToken.None);

        Assert.That(_hub.Sent, Has.Count.EqualTo(1));
        Assert.That(_context.Relationships.Count(), Is.EqualTo(1), "a redelivery must not materialize a second row");
    }

    [Test]
    public async Task AcceptedReceived_NoRemoteShadowProfile_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => SocialMaterializationHandlers.Handle(new FederatedFriendAcceptedReceived
        {
            EventId = "evt-4",
            OriginInstanceId = "instance-a",
            SenderId = "unknown-remote-user",
            InitiatorUserId = "local-user",
        }, _context, _hub, CancellationToken.None));
    }

    [Test]
    public async Task RejectedReceived_ExistingRelationship_SetsNone()
    {
        var localProfile = Profile.Create(new CreateProfileParams { UserId = "local-user", Username = "local" });
        var shadow = Profile.Create(new CreateProfileParams { UserId = "remote-user", Username = "remote-user" });
        _context.Profiles.AddRange(localProfile, shadow);
        await _context.SaveChangesAsync();

        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_2",
            OwnerId = localProfile.Id,
            TargetId = shadow.Id,
            Status = RelationshipStatus.PendingOutgoing,
        });
        await _context.SaveChangesAsync();

        await SocialMaterializationHandlers.Handle(new FederatedFriendRejectedReceived
        {
            EventId = "evt-5",
            OriginInstanceId = "instance-a",
            SenderId = "remote-user",
            InitiatorUserId = "local-user",
        }, _context, _hub, CancellationToken.None);

        var relationship = _context.Relationships.Single(r => r.Id == "rlsp_2");
        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.None));
    }

    [Test]
    public async Task RemovedReceived_ExistingRelationship_SetsNone()
    {
        var localProfile = Profile.Create(new CreateProfileParams { UserId = "local-user", Username = "local" });
        var shadow = Profile.Create(new CreateProfileParams { UserId = "remote-user", Username = "remote-user" });
        _context.Profiles.AddRange(localProfile, shadow);
        await _context.SaveChangesAsync();

        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_3",
            OwnerId = localProfile.Id,
            TargetId = shadow.Id,
            Status = RelationshipStatus.Friends,
        });
        await _context.SaveChangesAsync();

        await SocialMaterializationHandlers.Handle(new FederatedFriendRemovedReceived
        {
            EventId = "evt-6",
            OriginInstanceId = "instance-a",
            SenderId = "remote-user",
            TargetUserId = "local-user",
        }, _context, _hub, CancellationToken.None);

        var relationship = _context.Relationships.Single(r => r.Id == "rlsp_3");
        Assert.That(relationship.Status, Is.EqualTo(RelationshipStatus.None));
    }

    [Test]
    public async Task RemovedReceived_NoRemoteShadowProfile_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => SocialMaterializationHandlers.Handle(new FederatedFriendRemovedReceived
        {
            EventId = "evt-7",
            OriginInstanceId = "instance-a",
            SenderId = "unknown-remote-user",
            TargetUserId = "local-user",
        }, _context, _hub, CancellationToken.None));
    }
}
