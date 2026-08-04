using Domain;
using Messaging.Application.Services.Privacy;
using Messaging.Tests.Helpers;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Messaging.Tests.Services;

/// <summary>
/// T0-2's resolution table, one case per row, plus the two things that decide before it runs:
/// blocking, and a lookup that could not be performed.
/// </summary>
[TestFixture]
public class DirectMessagePolicyServiceTests
{
    private const string Initiator = "user-1";
    private const string Recipient = "user-2";

    private static ProfileDto Profile(string userId, params string[] friends) => new()
    {
        Id = "profile-" + userId,
        UserId = userId,
        UserName = userId,
        Hash = 1,
        Font = "Default",
        AvatarUrl = "",
        BannerUrl = "",
        Relationships = friends
            .Select(f => new RelationshipDto { Id = "rel-" + f, UserId = f, Status = RelationshipStatus.Accepted })
            .ToList(),
    };

    /// <summary>A bus that knows the recipient's profile - and therefore whether the initiator is
    /// on the recipient's friend list, which is the half T0-2 actually reads.</summary>
    private static FakeMessageBus ProfilesBus(bool recipientIsFriendsWithInitiator) =>
        new(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == Recipient => new GetProfileByUserIdResponse
            {
                Profile = recipientIsFriendsWithInitiator ? Profile(Recipient, Initiator) : Profile(Recipient),
            },
            GetProfileByUserIdRequest r => new GetProfileByUserIdResponse { Profile = Profile(r.UserId) },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });

    private static DirectMessagePolicyService Service(
        DirectMessagePolicy policy,
        bool friends = false,
        bool sharesGuild = false,
        IEnumerable<BlockRelationship>? blocks = null,
        bool blockLookupFails = false) =>
        TestPrivacyServices.Build(
            ProfilesBus(friends),
            [TestPrivacyServices.With(Recipient, s => s.DirectMessagePolicy = policy)],
            blocks,
            sharesGuild,
            blockLookupFails: blockLookupFails).Policy;

    // ── The table ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Everyone_AdmitsAStranger()
    {
        var refusal = await Service(DirectMessagePolicy.Everyone).EvaluateAsync(Initiator, [Recipient]);
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public async Task Nobody_RefusesEvenAFriend()
    {
        var refusal = await Service(DirectMessagePolicy.Nobody, friends: true)
            .EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.RecipientPolicy));
    }

    [Test]
    public async Task Friends_AdmitsAFriend()
    {
        var refusal = await Service(DirectMessagePolicy.Friends, friends: true)
            .EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Null);
    }

    [Test]
    public async Task Friends_RefusesANonFriend()
    {
        var refusal = await Service(DirectMessagePolicy.Friends).EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.RecipientPolicy));
        Assert.That(refusal.UserId, Is.EqualTo(Recipient));
    }

    [Test]
    public async Task FriendsAndServerMembers_AdmitsAFriendWithNoSharedGuild()
    {
        var refusal = await Service(DirectMessagePolicy.FriendsAndServerMembers, friends: true)
            .EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Null);
    }

    [Test]
    public async Task FriendsAndServerMembers_AdmitsAStrangerFromASharedGuild()
    {
        var refusal = await Service(DirectMessagePolicy.FriendsAndServerMembers, sharesGuild: true)
            .EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Null);
    }

    [Test]
    public async Task FriendsAndServerMembers_RefusesWhenNeitherHolds()
    {
        var refusal = await Service(DirectMessagePolicy.FriendsAndServerMembers)
            .EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.RecipientPolicy));
    }

    [Test]
    public async Task FriendsAndServerMembers_WithNoGuildLookupWiredUp_DegradesToFriendsOnly()
    {
        // The shipped state until Guild publishes T2-14's contract. Documented rather than hidden:
        // it is a visible behaviour difference, and it errs restrictive as the spec requires.
        var service = TestPrivacyServices.Build(
            ProfilesBus(recipientIsFriendsWithInitiator: false),
            [TestPrivacyServices.With(Recipient, s => s.DirectMessagePolicy = DirectMessagePolicy.FriendsAndServerMembers)],
            sharesGuild: false).Policy;

        var refusal = await service.EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.RecipientPolicy));
    }

    // ── Direction ─────────────────────────────────────────────────────────────

    [Test]
    public async Task TheRecipientsFriendListDecides_NotTheInitiators()
    {
        // The bug T0-2 exists to fix. The initiator considers the recipient a friend; the recipient
        // does not reciprocate and is on Friends-only. The old check read the initiator's list and
        // admitted this; the recipient's setting is what governs.
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == Initiator =>
                new GetProfileByUserIdResponse { Profile = Profile(Initiator, Recipient) },
            GetProfileByUserIdRequest r when r.UserId == Recipient =>
                new GetProfileByUserIdResponse { Profile = Profile(Recipient) },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var service = TestPrivacyServices.Build(
            bus,
            [TestPrivacyServices.With(Recipient, s => s.DirectMessagePolicy = DirectMessagePolicy.Friends)]).Policy;

        var refusal = await service.EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.RecipientPolicy));
    }

    // ── Blocking, which decides before policy ─────────────────────────────────

    [Test]
    public async Task TheBlockerIsToldTheyBlocked()
    {
        // Their own action, so naming it is not a leak - and "unblock them to message them" is the
        // only thing a client can usefully offer.
        var service = Service(
            DirectMessagePolicy.Everyone,
            blocks: [new BlockRelationship { BlockerId = Initiator, BlockedId = Recipient }]);

        var refusal = await service.EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.Blocked));
        Assert.That(refusal.UserId, Is.EqualTo(Recipient));
    }

    [Test]
    public async Task TheBlockedPartyIsNotToldTheyWereBlocked()
    {
        // T0-3: a block must be indistinguishable to the blocked party from ordinary
        // unfriendliness. It is reported with exactly the code a Friends-only recipient produces -
        // and note the recipient here is on Everyone, so the code is the *only* thing separating
        // this from a policy refusal, which is the point.
        var service = Service(
            DirectMessagePolicy.Everyone,
            blocks: [new BlockRelationship { BlockerId = Recipient, BlockedId = Initiator }]);

        var refusal = await service.EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.RecipientPolicy),
            "'blocked' here would tell the blocked party exactly what T0-3 says they must not learn");
    }

    [Test]
    public async Task ABlockRefusesBeforeAnyPolicyIsConsulted()
    {
        // Recipient is on Everyone and would otherwise admit anybody.
        var service = Service(
            DirectMessagePolicy.Everyone,
            friends: true,
            blocks: [new BlockRelationship { BlockerId = Initiator, BlockedId = Recipient }]);

        var refusal = await service.EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.Blocked));
    }

    // ── Degraded lookups ──────────────────────────────────────────────────────

    [Test]
    public async Task AnUnreachableBlockListRefuses_ButAsTransient()
    {
        var service = Service(DirectMessagePolicy.Everyone, blockLookupFails: true);

        var refusal = await service.EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.LookupUnavailable));
        Assert.That(refusal.IsTransient, Is.True,
            "A transient outage must not be presented to the user as a permission decision");
    }

    [Test]
    public async Task AnUnresolvableProfileIsNotAFriend()
    {
        // Fail closed: a Social outage must not turn a friends-only account into an open one.
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest => throw new InvalidOperationException("social is down"),
            _ => throw new InvalidOperationException("unexpected"),
        });

        var service = TestPrivacyServices.Build(
            bus,
            [TestPrivacyServices.With(Recipient, s => s.DirectMessagePolicy = DirectMessagePolicy.Friends)]).Policy;

        var refusal = await service.EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.RecipientPolicy));
    }

    [Test]
    public async Task AnUnreachableIdentityFallsBackToFriendsRatherThanEveryone()
    {
        var service = TestPrivacyServices.Build(
            ProfilesBus(recipientIsFriendsWithInitiator: false),
            privacyLookupFails: true).Policy;

        var refusal = await service.EvaluateAsync(Initiator, [Recipient]);

        Assert.That(refusal, Is.Not.Null, "Friends is the fail-closed default, and they are not friends");
        Assert.That(refusal!.Code, Is.EqualTo(DmRefusal.RecipientPolicy));
    }

    // ── Edges ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheInitiatorIsNeverEvaluatedAgainstThemselves()
    {
        // Conversation creation passes the whole member list, which can include the caller.
        var service = Service(DirectMessagePolicy.Nobody);

        var refusal = await service.EvaluateAsync(Initiator, [Initiator]);

        Assert.That(refusal, Is.Null);
    }

    [Test]
    public async Task AnEmptyRecipientListAdmits()
    {
        var refusal = await Service(DirectMessagePolicy.Nobody).EvaluateAsync(Initiator, []);
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public async Task OneRefusingRecipientRefusesTheWholeRequest()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r => new GetProfileByUserIdResponse { Profile = Profile(r.UserId, Initiator) },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var service = TestPrivacyServices.Build(bus,
        [
            TestPrivacyServices.With("ok-1", s => s.DirectMessagePolicy = DirectMessagePolicy.Everyone),
            TestPrivacyServices.With("closed", s => s.DirectMessagePolicy = DirectMessagePolicy.Nobody),
        ]).Policy;

        var refusal = await service.EvaluateAsync(Initiator, ["ok-1", "closed"]);

        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.UserId, Is.EqualTo("closed"));
    }

    [Test]
    public async Task ARecipientOnEveryoneCostsNoProfileLookup()
    {
        // Not a micro-optimisation: this runs on every one-to-one send, and a policy that needs no
        // friendship answer must not pay for one.
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest => throw new InvalidOperationException("must not be asked"),
            _ => throw new InvalidOperationException("unexpected"),
        });

        var service = TestPrivacyServices.Build(bus,
            [TestPrivacyServices.With(Recipient, s => s.DirectMessagePolicy = DirectMessagePolicy.Everyone)]).Policy;

        Assert.That(await service.EvaluateAsync(Initiator, [Recipient]), Is.Null);
    }
}
