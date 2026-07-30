using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Endpoints;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Conversation;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;
using ConversationDto = Messaging.Application.Dtos.Response.ConversationDto;

namespace Messaging.Tests.Endpoints;

/// <summary>
/// Covers ConversationEndpoints: FetchTokensForUsers' friendship gate, CreateConversation's
/// friendship-required-for-every-member rule (including the MLS device-token consumption branch),
/// and DeleteConversation's "last member deletes the row / any other member just leaves" split.
/// </summary>
[TestFixture]
public class ConversationEndpointsTests
{
    private TestMessagingContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestMessagingContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ProfileDto ProfileFor(string userId, params string[] friendUserIds) => new()
    {
        Id = "profile-" + userId,
        UserId = userId,
        UserName = "user-" + userId,
        Hash = 1,
        Font = "Default",
        AvatarUrl = "",
        BannerUrl = "",
        Relationships = friendUserIds.Select(f => new RelationshipDto { Id = "rel-" + f, UserId = f, Status = RelationshipStatus.Accepted }).ToList(),
    };

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

    // ══════════════════════════════════════════════════════════════════════════
    // FetchTokensForUsers
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FetchTokens_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.FetchTokensForUsers(
            new ConsumeMlsDeviceTokensForUserRequest { UserIds = ["user-2"] }, bus, TestPrincipal.Anonymous(), _context);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task FetchTokens_RequestingUserNotFriendsWithTarget_ReturnsBadRequest()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2") },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.FetchTokensForUsers(
            new ConsumeMlsDeviceTokensForUserRequest { UserIds = ["user-2"] }, bus, TestPrincipal.ForUser("user-1"), _context);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task FetchTokens_AllFriends_ReturnsOkWithTokens()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2") },
            ConsumeMlsDeviceTokensForUserRequest => new ConsumeMlsDeviceTokensForUserResponse
            {
                DeviceTokens = [new DeviceTokenResponse { UserId = "user-2", DeviceId = "device-1", Token = [1, 2, 3] }],
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.FetchTokensForUsers(
            new ConsumeMlsDeviceTokensForUserRequest { UserIds = ["user-2"] }, bus, TestPrincipal.ForUser("user-1"), _context);

        Assert.That(result, Is.InstanceOf<Ok<ConsumeMlsDeviceTokensForUserResponse>>());
        var ok = (Ok<ConsumeMlsDeviceTokensForUserResponse>)result;
        Assert.That(ok.Value!.DeviceTokens, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CreateConversation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateConversation_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] };

        var result = await endpoint.CreateConversation(dto, bus, TestPrincipal.Anonymous(), _context);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateConversation_RequestingUserProfileNotFound_ReturnsBadRequest()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest => new GetProfileByUserIdResponse { Profile = null },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateConversationDto { Members = [] };

        var result = await endpoint.CreateConversation(dto, bus, TestPrincipal.ForUser("user-1"), _context);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateConversation_MemberProfileNotFound_ReturnsBadRequest()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = null },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] };

        var result = await endpoint.CreateConversation(dto, bus, TestPrincipal.ForUser("user-1"), _context);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateConversation_MemberNotFriendedWithRequester_ReturnsBadRequest()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            // user-1's relationships are empty - user-2 is not a friend.
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2") },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] };

        var result = await endpoint.CreateConversation(dto, bus, TestPrincipal.ForUser("user-1"), _context);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateConversation_AllMembersFriended_PlainEncryption_CreatesConversation()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2", "user-1") },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateConversationDto
        {
            Encryption = ChannelEncryptionState.Plain,
            Members = [new CreateConversationMemberDto { UserId = "user-2" }],
        };

        var result = await endpoint.CreateConversation(dto, bus, TestPrincipal.ForUser("user-1"), _context);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
        var stored = await _context.Conversations.Include(c => c.Members).SingleAsync();
        Assert.That(stored.Members.Select(m => m.UserId), Is.EquivalentTo(new[] { "user-1", "user-2" }),
            "Both the requester and the invited member must be persisted");
    }

    [Test]
    public async Task CreateConversation_EncryptedConversation_ConsumesMlsDeviceTokens_AndCreates()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2", "user-1") },
            ConsumeMlsDeviceTokensForUserRequest => new ConsumeMlsDeviceTokensForUserResponse { DeviceTokens = [] },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateConversationDto
        {
            Encryption = ChannelEncryptionState.Encrypted,
            MlsGroupId = [1, 2, 3],
            MlsEpoch = 1,
            MlsGroupInfo = [4, 5, 6],
            Members = [new CreateConversationMemberDto { UserId = "user-2" }],
        };

        var result = await endpoint.CreateConversation(dto, bus, TestPrincipal.ForUser("user-1"), _context);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
        Assert.That(bus.Invoked.Any(m => m is ConsumeMlsDeviceTokensForUserRequest), Is.True);
    }

    [Test]
    public async Task CreateConversation_WithDeviceWelcomes_PersistsPendingWelcomes()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2", "user-1") },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateConversationDto
        {
            Members = [new CreateConversationMemberDto { UserId = "user-2" }],
            DeviceWelcomes = [new DeviceWelcomeDto { DeviceId = "device-1", UserId = "user-2", Welcome = [9, 9, 9] }],
        };

        await endpoint.CreateConversation(dto, bus, TestPrincipal.ForUser("user-1"), _context);
        await _context.SaveChangesAsync();

        Assert.That(_context.PendingWelcomes.Any(w => w.UserId == "user-2" && w.DeviceId == "device-1"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DeleteConversation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteConversation_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.DeleteConversation("conv-1", bus, TestPrincipal.Anonymous(), _context);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task DeleteConversation_RequesterNotAMember_ReturnsForbidden()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = [MakeMember("m-1", "other-user", "conv-1")],
        });
        await _context.SaveChangesAsync();

        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.DeleteConversation("conv-1", bus, TestPrincipal.ForUser("user-1"), _context);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteConversation_LastMember_DeletesConversation_AndPublishesConversationDeleted()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = [MakeMember("m-1", "user-1", "conv-1")],
        });
        await _context.SaveChangesAsync();

        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.DeleteConversation("conv-1", bus, TestPrincipal.ForUser("user-1"), _context);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok>());
            Assert.That(_context.Conversations.Any(c => c.Id == "conv-1"), Is.False);
            Assert.That(bus.Published.Any(p => p is ConversationDeleted), Is.True);
        });
    }

    [Test]
    public async Task DeleteConversation_OneOfMultipleMembers_RemovesJustThatMember_AndPublishesMemberRemoved()
    {
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members = [MakeMember("m-1", "user-1", "conv-1"), MakeMember("m-2", "user-2", "conv-1")],
        });
        await _context.SaveChangesAsync();

        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus();

        var result = await endpoint.DeleteConversation("conv-1", bus, TestPrincipal.ForUser("user-1"), _context);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok>());
            Assert.That(_context.Conversations.Any(c => c.Id == "conv-1"), Is.True, "Conversation must survive while another member remains");
            var stored = _context.Conversations.Include(c => c.Members).Single(c => c.Id == "conv-1");
            Assert.That(stored.Members.Select(m => m.UserId), Is.EquivalentTo(new[] { "user-2" }));
            Assert.That(bus.Published.Any(p => p is ConversationMemberRemoved evt && evt.HasLeft), Is.True);
        });
    }
}
