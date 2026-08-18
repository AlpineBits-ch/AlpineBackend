using System.Text;
using Echo.Realtime.Devices;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using ConversationDto = Messaging.Application.Dtos.Response.ConversationDto;
using MessageDto = Messaging.Application.Dtos.Response.MessageDto;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// T0-2 and the block enforcement points, at the endpoints rather than at the resolver: the exact
/// status and code each refusal produces, and - for every refusal - that nothing was written.
/// </summary>
[TestFixture]
public class PrivacyEnforcementEndpointTests
{
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

    /// <summary>Everybody is friends with everybody, so friendship never masks the thing under
    /// test. Blocking and policy are what these cases vary.</summary>
    private static FakeMessageBus FriendlyBus() => new(msg => msg switch
    {
        GetProfileByUserIdRequest r => new GetProfileByUserIdResponse
        {
            Profile = Profile(r.UserId, "user-1", "user-2", "user-3"),
        },
        CreateMessageCommand cmd => Message.Create(new CreateMessageParams
        {
            Content = cmd.Content,
            ConversationId = cmd.ConversationId,
            ChannelId = cmd.ChannelId,
            AuthorId = cmd.AuthorId,
        }),
        GetUserDevicesRequest => new GetUserDevicesResponse { Devices = [] },
        _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
    });

    private static HttpContext Http()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[DeviceIdentity.HeaderName] = "device-caller";
        return http;
    }

    private static void AssertRefusal(IResult result, string expectedCode, int expectedStatus)
    {
        Assert.That(result, Is.InstanceOf<JsonHttpResult<DmRefusalDto>>());
        var json = (JsonHttpResult<DmRefusalDto>)result;

        Assert.Multiple(() =>
        {
            Assert.That(json.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(json.Value!.Error, Is.EqualTo(expectedCode));
        });
    }

    private static ConversationMember MakeMember(string id, string userId, string conversationId) => new()
    {
        Id = id,
        UserId = userId,
        ConversationId = conversationId,
        PublicKey = [],
        CachedUserName = "test-user",
        CachedUserHash = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<Conversation> SeedConversation(string id, params string[] memberUserIds)
    {
        var conversation = new Conversation
        {
            Id = id,
            Name = memberUserIds.Length > 2 ? "Group" : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Members = memberUserIds.Select((u, i) => MakeMember($"{id}-m{i}", u, id)).ToList(),
        };
        _context.Conversations.Add(conversation);
        await _context.SaveChangesAsync();
        return conversation;
    }

    // ══════════════════════════════════════════════════════════════════════════ CreateConversation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateConversation_WithARecipientOnEveryone_SucceedsWithoutFriendship()
    {
        // The positive half of the direction fix: nobody is anybody's friend and it still works,
        // because the recipient said anyone may reach them.
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r => new GetProfileByUserIdResponse { Profile = Profile(r.UserId) },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var privacy = TestPrivacyServices.Build(bus,
            [TestPrivacyServices.With("user-2", s => s.DirectMessagePolicy = DirectMessagePolicy.Everyone)]);

        var result = await new ConversationEndpoints().CreateConversation(
            new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] },
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            TestMlsServices.Coverage(bus), Http(), privacy.Policy);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
        Assert.That(await _context.Conversations.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task CreateConversation_WithARecipientOnNobody_Is403AndWritesNothing()
    {
        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            [TestPrivacyServices.With("user-2", s => s.DirectMessagePolicy = DirectMessagePolicy.Nobody)]);

        var result = await new ConversationEndpoints().CreateConversation(
            new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] },
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            TestMlsServices.Coverage(bus), Http(), privacy.Policy);

        AssertRefusal(result, DmRefusal.RecipientPolicy, StatusCodes.Status403Forbidden);
        Assert.That(await _context.Conversations.AnyAsync(), Is.False);
    }

    [Test]
    public async Task CreateConversation_WithSomeoneTheCallerBlocked_Is403Blocked()
    {
        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            blocks: [new BlockRelationship { BlockerId = "user-1", BlockedId = "user-2" }]);

        var result = await new ConversationEndpoints().CreateConversation(
            new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] },
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            TestMlsServices.Coverage(bus), Http(), privacy.Policy);

        AssertRefusal(result, DmRefusal.Blocked, StatusCodes.Status403Forbidden);
        Assert.That(await _context.Conversations.AnyAsync(), Is.False);
    }

    [Test]
    public async Task CreateConversation_WithSomeoneWhoBlockedTheCaller_Is403ButNeverSaysBlocked()
    {
        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            blocks: [new BlockRelationship { BlockerId = "user-2", BlockedId = "user-1" }]);

        var result = await new ConversationEndpoints().CreateConversation(
            new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] },
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            TestMlsServices.Coverage(bus), Http(), privacy.Policy);

        AssertRefusal(result, DmRefusal.RecipientPolicy, StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task CreateConversation_WhenTheBlockListCannotBeRead_Is503NotAPermissionError()
    {
        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus, blockLookupFails: true);

        var result = await new ConversationEndpoints().CreateConversation(
            new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] },
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            TestMlsServices.Coverage(bus), Http(), privacy.Policy);

        AssertRefusal(result, DmRefusal.LookupUnavailable, StatusCodes.Status503ServiceUnavailable);
        Assert.That(await _context.Conversations.AnyAsync(), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // AddConversationMember
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AddMember_WhoIsBlockedByAnExistingMember_IsRefused()
    {
        // The route around a block: the adder has no quarrel with the candidate, but somebody
        // already in the room does, and adding them puts the blocked person back in front of them.
        await SeedConversation("conv-group", "user-1", "user-2", "user-4");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            blocks: [new BlockRelationship { BlockerId = "user-2", BlockedId = "user-3" }]);

        var (result, evt) = await new ConversationEndpoints().AddConversationMember(
            "conv-group", new AddConversationMemberDto { UserId = "user-3" },
            bus, TestPrincipal.ForUser("user-1"), _context, privacy.Policy, privacy.Blocks);

        AssertRefusal(result, DmRefusal.Blocked, StatusCodes.Status403Forbidden);
        Assert.That(evt, Is.Null);
        Assert.That(await _context.Members.CountAsync(m => m.ConversationId == "conv-group"), Is.EqualTo(3));
    }

    [Test]
    public async Task AddMember_WithNoBlockAnywhere_StillWorks()
    {
        await SeedConversation("conv-group", "user-1", "user-2", "user-4");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus);

        var (result, evt) = await new ConversationEndpoints().AddConversationMember(
            "conv-group", new AddConversationMemberDto { UserId = "user-3" },
            bus, TestPrincipal.ForUser("user-1"), _context, privacy.Policy, privacy.Blocks);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
        Assert.That(evt!.UserId, Is.EqualTo("user-3"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Sending into an existing conversation
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<IResult> SendAsync(
        string conversationId, string authorId, FakeMessageBus bus, TestPrivacyServices.Bundle privacy,
        IEnumerable<string>? attachmentIds = null)
    {
        var mls = new MlsGroupService(_context, new FakeMessagingHubContext(), bus,
            new MlsJoinRequestService(_context), TestMlsServices.Coverage(bus));

        return await new MessagingEndpoints().CreateMessage(
            new CreateMessageDto
            {
                Content = "hello",
                ConversationId = conversationId,
                Attachments = (attachmentIds ?? []).ToList(),
            },
            ScyllaContext.CreateDebug(), TestPrincipal.ForUser(authorId), _context, bus, _cache, mls,
            privacy.Policy, privacy.Content,
            new MessageLengthPolicy(bus, _cache, NullLogger<MessageLengthPolicy>.Instance));
    }

    [Test]
    public async Task SendInAOneToOneDm_WhenTheOtherSideBlockedTheSender_IsRefused()
    {
        await SeedConversation("conv-dm", "user-1", "user-2");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            blocks: [new BlockRelationship { BlockerId = "user-2", BlockedId = "user-1" }]);

        var result = await SendAsync("conv-dm", "user-1", bus, privacy);

        AssertRefusal(result, DmRefusal.RecipientPolicy, StatusCodes.Status403Forbidden);
        Assert.That(bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty, "nothing may be stored");
    }

    [Test]
    public async Task SendInAOneToOneDm_WhenTheSenderBlockedTheOtherSide_IsRefusedAsBlocked()
    {
        await SeedConversation("conv-dm", "user-1", "user-2");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            blocks: [new BlockRelationship { BlockerId = "user-1", BlockedId = "user-2" }]);

        var result = await SendAsync("conv-dm", "user-1", bus, privacy);

        AssertRefusal(result, DmRefusal.Blocked, StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task SendInAOneToOneDm_WhenTheOtherSideSwitchedToNobody_IsRefused()
    {
        // "A policy change governs new one-to-one sends" - the conversation already exists and the
        // sender is still a member, but the recipient has closed the door since.
        await SeedConversation("conv-dm", "user-1", "user-2");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            [TestPrivacyServices.With("user-2", s => s.DirectMessagePolicy = DirectMessagePolicy.Nobody)]);

        var result = await SendAsync("conv-dm", "user-1", bus, privacy);

        AssertRefusal(result, DmRefusal.RecipientPolicy, StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task SendInAOneToOneDm_WithNothingInTheWay_Succeeds()
    {
        await SeedConversation("conv-dm", "user-1", "user-2");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus);

        var result = await SendAsync("conv-dm", "user-1", bus, privacy);

        Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
    }

    [Test]
    public async Task SendInAGroup_IsNotReEvaluated_EvenAgainstAMemberOnNobody()
    {
        // Existing conversations are not retroactively closed.
        await SeedConversation("conv-group", "user-1", "user-2", "user-3");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
        [
            TestPrivacyServices.With("user-2", s => s.DirectMessagePolicy = DirectMessagePolicy.Nobody),
            TestPrivacyServices.With("user-3", s => s.DirectMessagePolicy = DirectMessagePolicy.Nobody),
        ],
            blocks: [new BlockRelationship { BlockerId = "user-2", BlockedId = "user-1" }]);

        var result = await SendAsync("conv-group", "user-1", bus, privacy);

        Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // T2-20 - explicit content filter on DM attachments
    // ══════════════════════════════════════════════════════════════════════════

    private async Task SeedAttachment(string id, string creatorId)
    {
        _context.Attachments.Add(Attachment.Create(new CreateAttachmentParams
        {
            Id = id,
            FileName = "picture.png",
            ContentType = "image/png",
            SizeBytes = 10,
            Url = "https://example.invalid/" + id,
            CreatorId = creatorId,
        }));
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task SendWithAnExplicitAttachment_IsRefusedWhenTheRecipientFiltersEveryone()
    {
        await SeedConversation("conv-dm", "user-1", "user-2");
        await SeedAttachment("atac_1", "user-1");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            [TestPrivacyServices.With("user-2", s => s.ExplicitContentFilter = ExplicitContentFilter.Everyone)],
            classifier: new TestPrivacyServices.StubMediaClassifier("atac_1"));

        var result = await SendAsync("conv-dm", "user-1", bus, privacy, ["atac_1"]);

        AssertRefusal(result, ExplicitContentGuard.RefusalCode, StatusCodes.Status403Forbidden);
        Assert.That(bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty);
    }

    [Test]
    public async Task SendWithAnExplicitAttachment_IsAllowedWhenTheRecipientHasTheFilterOff()
    {
        await SeedConversation("conv-dm", "user-1", "user-2");
        await SeedAttachment("atac_1", "user-1");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            [TestPrivacyServices.With("user-2", s => s.ExplicitContentFilter = ExplicitContentFilter.Off)],
            classifier: new TestPrivacyServices.StubMediaClassifier("atac_1"));

        var result = await SendAsync("conv-dm", "user-1", bus, privacy, ["atac_1"]);

        Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
    }

    [Test]
    public async Task SendWithAnExplicitAttachment_IsAllowedFromAFriendUnderUnknownSenders()
    {
        // FriendlyBus makes everybody friends, and UnknownSenders is exactly what its name says.
        await SeedConversation("conv-dm", "user-1", "user-2");
        await SeedAttachment("atac_1", "user-1");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            [TestPrivacyServices.With("user-2", s => s.ExplicitContentFilter = ExplicitContentFilter.UnknownSenders)],
            classifier: new TestPrivacyServices.StubMediaClassifier("atac_1"));

        var result = await SendAsync("conv-dm", "user-1", bus, privacy, ["atac_1"]);

        Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
    }

    [Test]
    public async Task SendWithAnAttachment_IsUnaffectedByTheShippedNoOpClassifier()
    {
        // The default deployment.
        await SeedConversation("conv-dm", "user-1", "user-2");
        await SeedAttachment("atac_1", "user-1");

        var bus = FriendlyBus();
        var privacy = TestPrivacyServices.Build(bus,
            [TestPrivacyServices.With("user-2", s => s.ExplicitContentFilter = ExplicitContentFilter.Everyone)]);

        var result = await SendAsync("conv-dm", "user-1", bus, privacy, ["atac_1"]);

        Assert.That(result, Is.InstanceOf<Created<MessageDto>>());
    }
}
