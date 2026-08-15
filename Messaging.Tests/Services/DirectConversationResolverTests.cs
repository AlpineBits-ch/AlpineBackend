using Messaging.Application.Services;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using Domain;

namespace Messaging.Tests.Services;

/// <summary>
/// "The DM with this person" - the question the server had never had to answer before, and the one
/// a server-side caller addressing a user rather than a conversation has to.
/// </summary>
[TestFixture]
public class DirectConversationResolverTests
{
    private const string Inviter = "user-inviter";
    private const string Target = "user-target";
    private const string Third = "user-third";

    private TestMessagingContext _context = null!;
    private FakeMessageBus _profiles = null!;

    /// <summary>Every profile the resolver asks for exists and is nobody's friend.</summary>
    private readonly Dictionary<string, ProfileDto> _byUserId = new(StringComparer.Ordinal);

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());

        foreach (var id in new[] { Inviter, Target, Third })
            _byUserId[id] = new ProfileDto { UserId = id, UserName = id, Hash = 1234 };

        _profiles = new FakeMessageBus(message => message switch
        {
            GetProfileByUserIdRequest r => new GetProfileByUserIdResponse
            {
                Profile = _byUserId.GetValueOrDefault(r.UserId),
            },
            _ => throw new InvalidOperationException($"No responder for {message.GetType().Name}"),
        });
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════════ Arrangement
    // ══════════════════════════════════════════════════════════════════════════

    private void Friends(params string[] userIds)
    {
        foreach (var id in userIds)
        {
            _byUserId[id].Relationships = userIds
                .Where(other => other != id)
                .Select(other => new RelationshipDto { UserId = other, Status = RelationshipStatus.Accepted })
                .ToList();
        }
    }

    private async Task<string> SeedConversationAsync(
        DateTimeOffset updatedAt, string? originInstanceId = null, params string[] userIds)
    {
        var id = Conversation.GenerateId();
        _context.Conversations.Add(new Conversation
        {
            Id = id,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            OriginInstanceId = originInstanceId,
            EncryptionState = ChannelEncryptionState.Plain,
            Members = userIds.Select(u => new ConversationMember
            {
                Id = ConversationMember.GenerateId(),
                ConversationId = id,
                UserId = u,
                PublicKey = [],
                CachedUserName = u,
                CachedUserHash = 1,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt,
            }).ToList(),
        });

        await _context.SaveChangesAsync();
        return id;
    }

    private DirectConversationResolver Resolver(
        bool privacyLookupFails = false,
        IEnumerable<Identity.Contracts.Bus.Response.UserPrivacySettingsSummary>? settings = null)
    {
        var privacy = TestPrivacyServices.Build(
            _profiles, settings: settings, privacyLookupFails: privacyLookupFails);

        return new DirectConversationResolver(
            _context, privacy.Policy, privacy.Bus, NullLogger<DirectConversationResolver>.Instance);
    }

    // ══════════════════════════════════════════════════════════════════════════ Finding one that
    // exists ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Finds_TheExistingOneToOneConversation()
    {
        var existing = await SeedConversationAsync(DateTimeOffset.UtcNow, null, Inviter, Target);

        var result = await Resolver().ResolveAsync(Inviter, Target);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectConversationOutcome.Found));
            Assert.That(result.ConversationId, Is.EqualTo(existing));
        });
    }

    [Test]
    public async Task Finds_TheMostRecentlyTouchedOne_WhenThePairHasSeveral()
    {
        var older = await SeedConversationAsync(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, Inviter, Target);
        var newer = await SeedConversationAsync(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), null, Target, Inviter);

        var result = await Resolver().ResolveAsync(Inviter, Target);

        Assert.Multiple(() =>
        {
            Assert.That(result.ConversationId, Is.EqualTo(newer));
            Assert.That(result.ConversationId, Is.Not.EqualTo(older),
                "picking arbitrarily drops the message into a thread neither of them has open");
        });
    }

    [Test]
    public async Task Finds_ItRegardlessOfWhichSideAsks()
    {
        var existing = await SeedConversationAsync(DateTimeOffset.UtcNow, null, Inviter, Target);

        var forward = await Resolver().ResolveAsync(Inviter, Target);
        var backward = await Resolver().ResolveAsync(Target, Inviter);

        Assert.That(forward.ConversationId, Is.EqualTo(backward.ConversationId).And.EqualTo(existing));
    }

    [Test]
    public async Task DoesNotUse_AGroupConversationThatHappensToContainBoth()
    {
        Friends(Inviter, Target);
        await SeedConversationAsync(DateTimeOffset.UtcNow, null, Inviter, Target, Third);

        var result = await Resolver().ResolveAsync(Inviter, Target);

        Assert.That(result.Outcome, Is.EqualTo(DirectConversationOutcome.Created),
            "a third person must not be shown an invitation addressed to somebody else");
    }

    [Test]
    public async Task DoesNotUse_AFederatedShadowConversation()
    {
        Friends(Inviter, Target);
        var shadow = await SeedConversationAsync(DateTimeOffset.UtcNow, "fedi-1", Inviter, Target);

        var result = await Resolver().ResolveAsync(Inviter, Target);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectConversationOutcome.Created));
            Assert.That(result.ConversationId, Is.Not.EqualTo(shadow),
                "the owning instance never agreed to it and would never see it");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Starting one that
    // does not ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Creates_APlainTwoMemberConversation_WhenThereIsNone()
    {
        Friends(Inviter, Target);

        var result = await Resolver().ResolveAsync(Inviter, Target);

        var created = await _context.Conversations
            .Include(c => c.Members)
            .SingleAsync(c => c.Id == result.ConversationId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectConversationOutcome.Created));
            Assert.That(created.Members.Select(m => m.UserId), Is.EquivalentTo(new[] { Inviter, Target }));
            Assert.That(created.EncryptionState, Is.EqualTo(ChannelEncryptionState.Plain),
                "the server holds no MLS key material and could not seal a Welcome to anybody");
            Assert.That(created.Members.Select(m => m.CachedUserName), Is.All.Not.Empty,
                "the cached name is how every conversation list renders the other party");
        });
    }

    [Test]
    public async Task Creates_ItOnlyOnce_AcrossTwoCalls()
    {
        Friends(Inviter, Target);
        var resolver = Resolver();

        var first = await resolver.ResolveAsync(Inviter, Target);
        var second = await resolver.ResolveAsync(Inviter, Target);

        Assert.Multiple(() =>
        {
            Assert.That(second.Outcome, Is.EqualTo(DirectConversationOutcome.Found));
            Assert.That(second.ConversationId, Is.EqualTo(first.ConversationId));
            Assert.That(_context.Conversations.Count(), Is.EqualTo(1));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Refusals
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Refuses_ToStartOneTheRecipientsPolicyWouldNotAdmit()
    {
        // Not friends, and the product default is friends-only.
        var result = await Resolver().ResolveAsync(Inviter, Target);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectConversationOutcome.Refused));
            Assert.That(result.HasConversation, Is.False);
            Assert.That(_context.Conversations.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task DoesNotApplyThePolicy_ToAConversationTheyAlreadyHave()
    {
        // Deliberately no friendship: an existing thread is proof they are already in contact, and
        // re-evaluating the setting against it would silently redirect the message away from
        // something both people can see.
        var existing = await SeedConversationAsync(DateTimeOffset.UtcNow, null, Inviter, Target);

        var result = await Resolver().ResolveAsync(Inviter, Target);

        Assert.That(result.ConversationId, Is.EqualTo(existing));
    }

    [Test]
    public async Task Creates_WhenTheRecipientAdmitsEveryone()
    {
        var open = TestPrivacyServices.With(Target, s => s.DirectMessagePolicy = DirectMessagePolicy.Everyone);

        var result = await Resolver(settings: [open]).ResolveAsync(Inviter, Target);

        Assert.That(result.Outcome, Is.EqualTo(DirectConversationOutcome.Created),
            "no friendship needed - the recipient said anybody may open one");
    }

    [Test]
    public async Task Refuses_WhenThePrivacyLookupIsDown_EvenForAnOpenRecipient()
    {
        // The recipient really does admit everyone, and Identity cannot say so.
        var open = TestPrivacyServices.With(Target, s => s.DirectMessagePolicy = DirectMessagePolicy.Everyone);

        var result = await Resolver(privacyLookupFails: true, settings: [open]).ResolveAsync(Inviter, Target);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectConversationOutcome.Refused));
            Assert.That(_context.Conversations.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task Refuses_WhenAProfileCannotBeResolved()
    {
        _byUserId.Remove(Target);

        var result = await Resolver().ResolveAsync(Inviter, Target);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectConversationOutcome.ProfileUnavailable));
            Assert.That(_context.Conversations.Count(), Is.Zero,
                "a member row written with a placeholder name is wrong in the database, not on one screen");
        });
    }

    [Test]
    public async Task Refuses_ToResolveSomebodyWithThemselves()
    {
        var result = await Resolver().ResolveAsync(Inviter, Inviter);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectConversationOutcome.SameUser));
            Assert.That(_context.Conversations.Count(), Is.Zero);
        });
    }
}
