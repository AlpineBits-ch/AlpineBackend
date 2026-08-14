using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Endpoints;
using Messaging.Application.Services;
using Messaging.Application.Services.Privacy;
using Messaging.Contracts.Bus.Commands;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Infrastructure.Persistence;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MessageDto = global::Messaging.Application.Dtos.Response.MessageDto;

namespace Messaging.Tests.Endpoints;

/// <summary>R5/R19: a role mention is a permission, not a client decision.</summary>
[TestFixture]
public class RoleMentionGatingTests
{
    private const string ChannelId = "chan-1";
    private const string UserId = "user-1";

    private TestMessagingContext _context = null!;
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private MlsGroupService MakeMlsService(FakeMessageBus bus) =>
        new(_context, new FakeMessagingHubContext(), bus, new MlsJoinRequestService(_context),
            TestMlsServices.Coverage(bus));

    /// <summary>Guild's answers for one send: which permissions the author holds, and how the
    /// channel's guild classifies each role id it was asked about.</summary>
    private static FakeMessageBus Bus(
        ExternalPermission[] granted,
        string[] mentionable,
        string[] restricted) =>
        new(msg => msg switch
        {
            HasUserPermissionToChannelRequest r => new HasUserPermissionToChannelResponse
            {
                IsAllowed = granted.Contains(r.Permission), Permission = r.Permission,
            },
            ResolveRoleMentionsRequest r => new ResolveRoleMentionsResponse
            {
                MentionableRoleIds = mentionable.Where(r.RoleIds.Contains).ToList(),
                RestrictedRoleIds = restricted.Where(r.RoleIds.Contains).ToList(),
            },
            GetGuildAutoModConfigRequest => new GetGuildAutoModConfigResponse { Enabled = false },
            CreateMessageCommand c => Message.Create(new CreateMessageParams
            {
                Content = c.Content, ChannelId = c.ChannelId, ConversationId = c.ConversationId,
                AuthorId = c.AuthorId, Mentions = c.Mentions, RoleMentions = c.RoleMentions,
                MentionsEveryone = c.MentionsEveryone, MentionsHere = c.MentionsHere,
            }),
            _ => throw new InvalidOperationException($"unexpected {msg.GetType().Name}"),
        });

    private Task<IResult> SendAsync(FakeMessageBus bus, CreateMessageDto dto)
    {
        var endpoint = new MessagingEndpoints();
        return endpoint.CreateMessage(dto, ScyllaContext.CreateDebug(), TestPrincipal.ForUser(UserId),
            _context, bus, _cache, MakeMlsService(bus),
            TestPrivacyServices.Build(bus).Policy, TestPrivacyServices.Build(bus).Content);
    }

    private static CreateMessageCommand CommandOn(FakeMessageBus bus) =>
        bus.Invoked.OfType<CreateMessageCommand>().Single();

    private static CreateMessageDto ChannelMessage(params string[] roleMentions) => new()
    {
        Content = "ping", ChannelId = ChannelId, RoleMentions = roleMentions,
    };

    /// <summary>SendMessages plus nothing else: an ordinary member.</summary>
    private static readonly ExternalPermission[] OrdinaryMember = [ExternalPermission.SendMessages];

    private static readonly ExternalPermission[] Moderator =
        [ExternalPermission.SendMessages, ExternalPermission.MentionEveryone];

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task MentionableRole_IsKept()
    {
        var bus = Bus(OrdinaryMember, mentionable: ["role-open"], restricted: []);

        var result = await SendAsync(bus, ChannelMessage("role-open"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(CommandOn(bus).RoleMentions, Is.EqualTo(new[] { "role-open" }));
        });
    }

    // ── The gate ──────────────────────────────────────────────────────────────

    [Test]
    public async Task NonMentionableRole_IsStrippedForAnOrdinaryMember_AndTheMessageStillSends()
    {
        var bus = Bus(OrdinaryMember, mentionable: [], restricted: ["role-staff"]);

        var result = await SendAsync(bus, ChannelMessage("role-staff"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>(),
                "stripped, not rejected - the markup is part of the text");
            Assert.That(CommandOn(bus).RoleMentions, Is.Empty);
        });
    }

    [Test]
    public async Task NonMentionableRole_IsKeptForAMentionEveryoneHolder()
    {
        var bus = Bus(Moderator, mentionable: [], restricted: ["role-staff"]);

        var result = await SendAsync(bus, ChannelMessage("role-staff"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(CommandOn(bus).RoleMentions, Is.EqualTo(new[] { "role-staff" }));
        });
    }

    [Test]
    public async Task MixedList_KeepsTheMentionableAndDropsTheRest()
    {
        var bus = Bus(OrdinaryMember, mentionable: ["role-open"], restricted: ["role-staff"]);

        var result = await SendAsync(bus, ChannelMessage("role-open", "role-staff"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(CommandOn(bus).RoleMentions, Is.EqualTo(new[] { "role-open" }));
        });
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public async Task RoleOfAnotherGuild_IsDropped()
    {
        // Guild classifies it as neither mentionable nor restricted, which is how a foreign id
        // reaches this endpoint.
        var bus = Bus(Moderator, mentionable: [], restricted: []);

        var result = await SendAsync(bus, ChannelMessage("role-foreign"));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(CommandOn(bus).RoleMentions, Is.Empty);
        });
    }

    [Test]
    public async Task ConversationScope_DropsEveryRoleMention()
    {
        // A DM or group has no roles at all, so there is nothing to validate the ids against and
        // nothing they could legitimately mean.
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Members =
            [
                new ConversationMember
                {
                    Id = "cmem-1", UserId = UserId, ConversationId = "conv-1", PublicKey = [],
                    CachedUserName = "u", CachedUserHash = 0,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        });
        await _context.SaveChangesAsync();

        var bus = Bus(OrdinaryMember, mentionable: ["role-open"], restricted: []);

        var result = await SendAsync(bus, new CreateMessageDto
        {
            Content = "ping", ConversationId = "conv-1", RoleMentions = ["role-open", "role-staff"],
        });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
            Assert.That(CommandOn(bus).RoleMentions, Is.Empty);
            Assert.That(bus.Invoked.OfType<ResolveRoleMentionsRequest>(), Is.Empty,
                "there is nothing to ask Guild about");
        });
    }

    // ── Cost ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task ManyRoles_CostOneResolutionRequest()
    {
        // The property that keeps this affordable: N roles is one round-trip, not N. The cap is
        // MaxMentionsPerMessage, so this is the worst case the endpoint accepts.
        var requested = Enumerable.Range(0, MessagingEndpoints.MaxMentionsPerMessage)
            .Select(i => $"role-{i}").ToArray();

        var bus = Bus(OrdinaryMember, mentionable: requested, restricted: []);

        await SendAsync(bus, ChannelMessage(requested));

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<ResolveRoleMentionsRequest>().Count(), Is.EqualTo(1));
            Assert.That(bus.Invoked.OfType<ResolveRoleMentionsRequest>().Single().RoleIds,
                Has.Count.EqualTo(MessagingEndpoints.MaxMentionsPerMessage));
            Assert.That(CommandOn(bus).RoleMentions, Has.Count.EqualTo(MessagingEndpoints.MaxMentionsPerMessage));
        });
    }

    [Test]
    public async Task NoRoleMentions_AsksGuildNothingAboutRoles()
    {
        var bus = Bus(OrdinaryMember, mentionable: [], restricted: []);

        await SendAsync(bus, ChannelMessage());

        Assert.That(bus.Invoked.OfType<ResolveRoleMentionsRequest>(), Is.Empty);
    }

    [Test]
    public async Task AllRolesMentionable_NeverAsksAboutMentionEveryone()
    {
        // The permission is only consulted when something actually needs it, which is what keeps
        // the common send at one extra round-trip rather than two.
        var bus = Bus(OrdinaryMember, mentionable: ["role-open"], restricted: []);

        await SendAsync(bus, ChannelMessage("role-open"));

        Assert.That(bus.Invoked.OfType<HasUserPermissionToChannelRequest>()
                .Count(r => r.Permission == ExternalPermission.MentionEveryone), Is.Zero);
    }

    [Test]
    public async Task EveryoneFlagAndARestrictedRole_ResolveMentionEveryoneOnce()
    {
        // Both gates ask the same question; asking it twice would be a wasted round-trip on the
        // hottest path in the service.
        var bus = Bus(OrdinaryMember, mentionable: [], restricted: ["role-staff"]);

        var dto = ChannelMessage("role-staff");
        dto.MentionsEveryone = true;

        await SendAsync(bus, dto);

        Assert.Multiple(() =>
        {
            Assert.That(bus.Invoked.OfType<HasUserPermissionToChannelRequest>()
                .Count(r => r.Permission == ExternalPermission.MentionEveryone), Is.EqualTo(1));
            Assert.That(CommandOn(bus).MentionsEveryone, Is.False);
            Assert.That(CommandOn(bus).RoleMentions, Is.Empty);
        });
    }

    [Test]
    public async Task ClientOrderSurvivesTheGate()
    {
        var bus = Bus(Moderator, mentionable: ["role-b"], restricted: ["role-a", "role-c"]);

        await SendAsync(bus, ChannelMessage("role-c", "role-a", "role-b"));

        Assert.That(CommandOn(bus).RoleMentions, Is.EqualTo(new[] { "role-c", "role-a", "role-b" }));
    }
}
