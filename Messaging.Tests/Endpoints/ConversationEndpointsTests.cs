using Domain;
using Echo.Realtime.Devices;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Messaging.Application.Dtos.Request;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Endpoints;
using Messaging.Application.Services.Privacy;
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

using static Messaging.Tests.Helpers.TestMlsServices;

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
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>
    /// The T0-2 resolver over a test's own bus, with every account on the product default
    /// (<c>DirectMessagePolicy.Friends</c>) and nobody blocked.
    /// </summary>
    private static DirectMessagePolicyService Policy(FakeMessageBus bus) =>
        TestPrivacyServices.Build(bus).Policy;

    private static BlockCache Blocks(FakeMessageBus bus) =>
        TestPrivacyServices.Build(bus).Blocks;

    /// <summary>Every T0-2 refusal is the same shape: a status and a machine-readable code the
    /// client can branch on.</summary>
    private static void AssertRefusal(IResult result, string expectedCode, int expectedStatus, string? expectedUserId = null)
    {
        Assert.That(result, Is.InstanceOf<JsonHttpResult<DmRefusalDto>>());
        var json = (JsonHttpResult<DmRefusalDto>)result;

        Assert.Multiple(() =>
        {
            Assert.That(json.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(json.Value!.Error, Is.EqualTo(expectedCode));
            if (expectedUserId is not null) Assert.That(json.Value.UserId, Is.EqualTo(expectedUserId));
        });
    }

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

    private static HttpContext Http(string? callingDeviceId = "device-caller") =>
        TestMlsServices.HttpWithDevice(callingDeviceId);

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
            new ConsumeMlsDeviceTokensForUserRequest { UserIds = ["user-2"] }, bus, TestPrincipal.Anonymous(), _context, _cache, Policy(bus));

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task FetchTokens_TargetDoesNotAdmitTheCaller_IsForbidden()
    {
        // Was a 400 with a prose string.
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2") },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.FetchTokensForUsers(
            new ConsumeMlsDeviceTokensForUserRequest { UserIds = ["user-2"] }, bus, TestPrincipal.ForUser("user-1"), _context, _cache, Policy(bus));

        AssertRefusal(result, DmRefusal.RecipientPolicy, StatusCodes.Status403Forbidden, "user-2");
    }

    [Test]
    public async Task FetchTokens_AllFriends_ReturnsOkWithTokens()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            // user-2 counts user-1 as a friend, which is now the half that matters: T0-2 reads the
            // recipient's relationship list.
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2", "user-1") },
            ConsumeMlsDeviceTokensForUserRequest => new ConsumeMlsDeviceTokensForUserResponse
            {
                DeviceTokens = [new DeviceTokenResponse { UserId = "user-2", DeviceId = "device-1", Token = [1, 2, 3] }],
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await endpoint.FetchTokensForUsers(
            new ConsumeMlsDeviceTokensForUserRequest { UserIds = ["user-2"] }, bus, TestPrincipal.ForUser("user-1"), _context, _cache, Policy(bus));

        Assert.That(result, Is.InstanceOf<Ok<ConsumeMlsDeviceTokensForUserResponse>>());
        var ok = (Ok<ConsumeMlsDeviceTokensForUserResponse>)result;
        Assert.That(ok.Value!.DeviceTokens, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════ CreateConversation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateConversation_Unauthenticated_ReturnsUnauthorized()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus();
        var dto = new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] };

        var result = await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.Anonymous(), _context, Coverage(bus), Http(), Policy(bus));

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

        var result = await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));

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

        var result = await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateConversation_RecipientOnFriendsOnlyAndNotAFriend_IsForbidden()
    {
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            // Nobody is anybody's friend.
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2") },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateConversationDto { Members = [new CreateConversationMemberDto { UserId = "user-2" }] };

        var result = await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));

        AssertRefusal(result, DmRefusal.RecipientPolicy, StatusCodes.Status403Forbidden, "user-2");
        Assert.That(await _context.Conversations.AnyAsync(), Is.False);
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

        var result = await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
        var stored = await _context.Conversations.Include(c => c.Members).SingleAsync();
        Assert.That(stored.Members.Select(m => m.UserId), Is.EquivalentTo(new[] { "user-1", "user-2" }),
            "Both the requester and the invited member must be persisted");
    }

    [Test]
    public async Task CreateConversation_Encrypted_DoesNotConsumeTokensAgain()
    {
        // The client consumed one key package per invitee device before it built the group - those
        // packages are what the Welcomes were sealed to.
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2", "user-1") },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateConversationDto
        {
            Encryption = ChannelEncryptionState.Encrypted,
            MlsGroupId = [1, 2, 3],
            MlsEpoch = 1,
            MlsGroupInfo = [4, 5, 6],
            Members = [new CreateConversationMemberDto { UserId = "user-2" }],
            DeviceWelcomes = [new DeviceWelcomeDto { DeviceId = "device-2", UserId = "user-2", Welcome = [9, 9, 9] }],
        };

        var result = await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
            Assert.That(bus.Invoked.Any(m => m is ConsumeMlsDeviceTokensForUserRequest), Is.False,
                "The client already consumed the tokens it built the group from");
        });
    }

    /// <summary>Bus that knows both users, is friendly, and reports the given devices for user-2.</summary>
    private static FakeMessageBus EncryptedCreationBus(params string[] userTwoDeviceIds) =>
        new(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2", "user-1") },
            GetUserDevicesRequest => new GetUserDevicesResponse
            {
                Devices = userTwoDeviceIds.Select(d => new UserDeviceSummaryResponse
                {
                    UserId = "user-2", ClientDeviceId = d, DeviceName = d,
                }).ToList(),
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

    private static CreateConversationDto EncryptedDto(params DeviceWelcomeDto[] welcomes) => new()
    {
        Encryption = ChannelEncryptionState.Encrypted,
        MlsGroupId = [1, 2, 3],
        MlsEpoch = 1,
        MlsGroupInfo = [4, 5, 6],
        Members = [new CreateConversationMemberDto { UserId = "user-2" }],
        DeviceWelcomes = welcomes.ToList(),
    };

    [Test]
    public async Task CreateConversation_Encrypted_MemberWithTwoDevicesAndOneWelcome_ReportsTheMissingDevice()
    {
        // The check used to ask only whether the user appeared in the Welcome list at all, so a
        // member whose laptop was welcomed and whose phone was not passed silently.
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus("device-laptop", "device-phone");

        var dto = EncryptedDto(new DeviceWelcomeDto { DeviceId = "device-laptop", UserId = "user-2", Welcome = [9] });

        var result = await endpoint.CreateConversation(
            dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));

        var created = (Ok<ConversationDto>)result;
        Assert.That(created.Value!.UnreachableDevices.Select(d => d.DeviceId),
            Is.EquivalentTo(new[] { "device-phone" }));
    }

    /// <summary>Bus that knows both users and reports devices for whichever of them is asked about.</summary>
    private static FakeMessageBus EncryptedCreationBus(
        IReadOnlyList<string> userOneDeviceIds,
        IReadOnlyList<string> userTwoDeviceIds) =>
        new(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2", "user-1") },
            GetUserDevicesRequest r => new GetUserDevicesResponse
            {
                Devices = new[] { ("user-1", userOneDeviceIds), ("user-2", userTwoDeviceIds) }
                    .Where(pair => r.UserIds.Contains(pair.Item1))
                    .SelectMany(pair => pair.Item2.Select(d => new UserDeviceSummaryResponse
                    {
                        UserId = pair.Item1, ClientDeviceId = d, DeviceName = d,
                    }))
                    .ToList(),
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

    [Test]
    public async Task CreateConversation_Encrypted_CreatorsOwnSecondDeviceWithNoWelcome_IsReported()
    {
        // The reported failure, from the side nobody was looking at.
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus(
            userOneDeviceIds: ["device-caller", "device-desktop"],
            userTwoDeviceIds: ["device-2"]);

        var dto = EncryptedDto(new DeviceWelcomeDto { DeviceId = "device-2", UserId = "user-2", Welcome = [9] });

        var result = await endpoint.CreateConversation(
            dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            Coverage(bus), Http("device-caller"), Policy(bus));

        var created = (Ok<ConversationDto>)result;
        Assert.That(created.Value!.UnreachableDevices.Select(d => (d.UserId, d.DeviceId)),
            Is.EquivalentTo(new[] { ("user-1", "device-desktop") }));
    }

    [Test]
    public async Task CreateConversation_Encrypted_TheCreatingDeviceItselfIsNeverReported()
    {
        // It holds the group directly and so has no Welcome addressed to it.
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus(userOneDeviceIds: ["device-caller"], userTwoDeviceIds: ["device-2"]);

        var result = await endpoint.CreateConversation(
            EncryptedDto(new DeviceWelcomeDto { DeviceId = "device-2", UserId = "user-2", Welcome = [9] }),
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            Coverage(bus), Http("device-caller"), Policy(bus));

        Assert.That(((Ok<ConversationDto>)result).Value!.UnreachableDevices, Is.Empty);
    }

    [Test]
    public async Task CreateConversation_Encrypted_RecordsTheDeviceThatBuiltTheGroup()
    {
        // The report above is one moment, handed to one client.
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus(userOneDeviceIds: ["device-caller"], userTwoDeviceIds: ["device-2"]);

        await endpoint.CreateConversation(
            EncryptedDto(new DeviceWelcomeDto { DeviceId = "device-2", UserId = "user-2", Welcome = [9] }),
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            Coverage(bus), Http("device-caller"), Policy(bus));

        await _context.SaveChangesAsync();

        var generation = await _context.MlsGroupGenerations.SingleAsync();
        Assert.That(generation.ActivatedByDeviceId, Is.EqualTo("device-caller"));
    }

    [Test]
    public async Task CreateConversation_Encrypted_WithNoDeviceIdHeader_LeavesTheActivatingDeviceUnknown()
    {
        // Null means "we do not know which device", and every reader has to treat it that way rather
        // than as "no device" - the same degradation an old client already gets on the report.
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus(userOneDeviceIds: ["device-caller"], userTwoDeviceIds: ["device-2"]);

        await endpoint.CreateConversation(
            EncryptedDto(new DeviceWelcomeDto { DeviceId = "device-2", UserId = "user-2", Welcome = [9] }),
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            Coverage(bus), Http(callingDeviceId: null), Policy(bus));

        await _context.SaveChangesAsync();

        Assert.That((await _context.MlsGroupGenerations.SingleAsync()).ActivatedByDeviceId, Is.Null);
    }

    [Test]
    public async Task CreateConversation_Encrypted_WithNoDeviceIdHeader_DoesNotAccuseTheCreatorsOwnDevices()
    {
        // Without the header there is no way to tell which of the caller's devices is the one
        // asking, so they are left out of the scan rather than reported wholesale.
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus(
            userOneDeviceIds: ["device-caller", "device-desktop"],
            userTwoDeviceIds: ["device-2", "device-2-spare"]);

        var result = await endpoint.CreateConversation(
            EncryptedDto(new DeviceWelcomeDto { DeviceId = "device-2", UserId = "user-2", Welcome = [9] }),
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context,
            Coverage(bus), Http(callingDeviceId: null), Policy(bus));

        Assert.That(((Ok<ConversationDto>)result).Value!.UnreachableDevices.Select(d => (d.UserId, d.DeviceId)),
            Is.EquivalentTo(new[] { ("user-2", "device-2-spare") }));
    }

    [Test]
    public async Task CreateConversation_Encrypted_WithFullDeviceCoverage_ReportsNothing()
    {
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus("device-laptop", "device-phone");

        var dto = EncryptedDto(
            new DeviceWelcomeDto { DeviceId = "device-laptop", UserId = "user-2", Welcome = [9] },
            new DeviceWelcomeDto { DeviceId = "device-phone", UserId = "user-2", Welcome = [8] });

        var result = await endpoint.CreateConversation(
            dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));

        Assert.That(((Ok<ConversationDto>)result).Value!.UnreachableDevices, Is.Empty);
    }

    [Test]
    public async Task CreateConversation_Encrypted_IsPermissiveByDefaultDuringRollout()
    {
        // Clients in the field cannot pass the override flag.
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus("device-phone");

        var result = await endpoint.CreateConversation(
            EncryptedDto(), allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
        Assert.That(await _context.Conversations.AnyAsync(), Is.True);
    }

    [Test]
    public async Task CreateConversation_Encrypted_RefusesOnceThePolicyIsFlipped()
    {
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus("device-phone");

        MlsPolicy.RejectUnreachableDevicesOnCreate = true;
        try
        {
            var result = await endpoint.CreateConversation(
                EncryptedDto(), allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));

            Assert.That(result, Is.InstanceOf<BadRequest<CreateConversationRejectedDto>>());
            var rejection = (BadRequest<CreateConversationRejectedDto>)result;
            Assert.That(rejection.Value!.UnreachableDevices.Select(d => d.DeviceId),
                Is.EquivalentTo(new[] { "device-phone" }));
            Assert.That(await _context.Conversations.AnyAsync(), Is.False);
        }
        finally
        {
            MlsPolicy.RejectUnreachableDevicesOnCreate = false;
        }
    }

    [Test]
    public async Task CreateConversation_Encrypted_OverrideFlagCreatesAnyway()
    {
        var endpoint = new ConversationEndpoints();
        var bus = EncryptedCreationBus("device-phone");

        MlsPolicy.RejectUnreachableDevicesOnCreate = true;
        try
        {
            var result = await endpoint.CreateConversation(
                EncryptedDto(), allowPartialDeviceCoverage: true, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));

            // The caller said, explicitly, that it understands some devices will never read this.
            Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
            Assert.That(((Ok<ConversationDto>)result).Value!.UnreachableDevices, Has.Count.EqualTo(1));
        }
        finally
        {
            MlsPolicy.RejectUnreachableDevicesOnCreate = false;
        }
    }

    [Test]
    public async Task CreateConversation_Encrypted_WhenIdentityIsUnreachable_StillCreates()
    {
        // A degraded reachability report beats being unable to start a conversation because a
        // sibling service is down.
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2", "user-1") },
            _ => throw new InvalidOperationException("identity is down"),
        });

        var result = await endpoint.CreateConversation(
            EncryptedDto(new DeviceWelcomeDto { DeviceId = "device-2", UserId = "user-2", Welcome = [9] }),
            allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));

        Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
    }

    [Test]
    public async Task CreateConversation_Encrypted_StoresContextIdAndEpochOnWelcome()
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
            Encryption = ChannelEncryptionState.Encrypted,
            MlsGroupId = [1, 2, 3],
            MlsEpoch = 4,
            MlsGroupInfo = [4, 5, 6],
            Members = [new CreateConversationMemberDto { UserId = "user-2" }],
            DeviceWelcomes = [new DeviceWelcomeDto { DeviceId = "device-2", UserId = "user-2", Welcome = [9] }],
        };

        await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));
        await _context.SaveChangesAsync();

        var conversation = await _context.Conversations.SingleAsync();
        var welcome = await _context.PendingWelcomes.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(welcome.ContextId, Is.EqualTo(conversation.Id));
            Assert.That(welcome.ConversationId, Is.EqualTo(conversation.Id));
            Assert.That(welcome.Generation, Is.EqualTo(1));
            Assert.That(welcome.Epoch, Is.EqualTo(4), "The joining device needs to know which epoch to catch up from");
            Assert.That(welcome.ConsumedAt, Is.Null);
        });
    }

    [Test]
    public async Task CreateConversation_Encrypted_MintsGenerationOne()
    {
        // Without this the context reads as plaintext everywhere it matters: the send path would
        // refuse the very ciphertext the creating client is about to produce, and no commit could
        // be published against the group it just built.
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1", "user-2") },
            GetProfileByUserIdRequest r when r.UserId == "user-2" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-2", "user-1") },
            _ => throw new InvalidOperationException("unexpected"),
        });
        var dto = new CreateConversationDto
        {
            Encryption = ChannelEncryptionState.Encrypted,
            MlsGroupId = [1, 2, 3],
            MlsEpoch = 1,
            MlsGroupInfo = [4, 5, 6],
            Members = [new CreateConversationMemberDto { UserId = "user-2" }],
            DeviceWelcomes = [new DeviceWelcomeDto { DeviceId = "device-2", UserId = "user-2", Welcome = [9] }],
        };

        await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));
        await _context.SaveChangesAsync();

        var conversation = await _context.Conversations.SingleAsync();
        var generation = await _context.MlsGroupGenerations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(generation.Generation, Is.EqualTo(1));
            Assert.That(generation.State, Is.EqualTo(MlsGenerationState.Active));
            Assert.That(generation.ContextId, Is.EqualTo(conversation.Id));
            Assert.That(generation.MlsGroupId, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(generation.Epoch, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CreateConversation_Plain_MintsNoGeneration()
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

        await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));
        await _context.SaveChangesAsync();

        Assert.That(await _context.MlsGroupGenerations.AnyAsync(), Is.False);
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

        await endpoint.CreateConversation(dto, allowPartialDeviceCoverage: false, bus, TestPrincipal.ForUser("user-1"), _context, Coverage(bus), Http(), Policy(bus));
        await _context.SaveChangesAsync();

        Assert.That(_context.PendingWelcomes.Any(w => w.UserId == "user-2" && w.DeviceId == "device-1"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // AddConversationMember
    // ══════════════════════════════════════════════════════════════════════════

    private static FakeMessageBus FriendlyBus() => new(msg => msg switch
    {
        GetProfileByUserIdRequest r => new GetProfileByUserIdResponse
        {
            Profile = ProfileFor(r.UserId, "user-1", "user-2", "user-3"),
        },
        _ => throw new InvalidOperationException("unexpected"),
    });

    private async Task<Conversation> SeedGroupConversation(params string[] memberUserIds)
    {
        var conversation = new Conversation
        {
            Id = "conv-group",
            Name = "Study group",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Members = memberUserIds.Select((u, i) => MakeMember($"m-{i}", u, "conv-group")).ToList(),
        };
        _context.Conversations.Add(conversation);
        await _context.SaveChangesAsync();
        return conversation;
    }

    [Test]
    public async Task AddMember_ByANonMember_ReturnsForbidden()
    {
        await SeedGroupConversation("user-1", "user-2", "user-4");
        var endpoint = new ConversationEndpoints();

        var (result, _) = await endpoint.AddConversationMember(
            "conv-group", new AddConversationMemberDto { UserId = "user-3" },
            FriendlyBus(), TestPrincipal.ForUser("outsider"), _context, Policy(FriendlyBus()), Blocks(FriendlyBus()));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task AddMember_ToAGroup_AddsThemAndAnnouncesIt()
    {
        await SeedGroupConversation("user-1", "user-2", "user-4");
        var endpoint = new ConversationEndpoints();

        var (result, evt) = await endpoint.AddConversationMember(
            "conv-group", new AddConversationMemberDto { UserId = "user-3" },
            FriendlyBus(), TestPrincipal.ForUser("user-1"), _context, Policy(FriendlyBus()), Blocks(FriendlyBus()));
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
            Assert.That(evt!.UserId, Is.EqualTo("user-3"));
            Assert.That(await _context.Members.CountAsync(m => m.ConversationId == "conv-group"), Is.EqualTo(4));
        });
    }

    [Test]
    public async Task AddMember_ToATwoPersonDm_IsRefused()
    {
        // Growing a 1:1 into a group silently changes what the other person thought they were in.
        _context.Conversations.Add(new Conversation
        {
            Id = "conv-group",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Members = [MakeMember("m-0", "user-1", "conv-group"), MakeMember("m-1", "user-2", "conv-group")],
        });
        await _context.SaveChangesAsync();
        var endpoint = new ConversationEndpoints();

        var (result, _) = await endpoint.AddConversationMember(
            "conv-group", new AddConversationMemberDto { UserId = "user-3" },
            FriendlyBus(), TestPrincipal.ForUser("user-1"), _context, Policy(FriendlyBus()), Blocks(FriendlyBus()));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task AddMember_WhoDoesNotAdmitTheAdder_IsRefused()
    {
        await SeedGroupConversation("user-1", "user-2", "user-4");
        var endpoint = new ConversationEndpoints();
        var bus = new FakeMessageBus(msg => msg switch
        {
            // user-1 is friends with nobody relevant.
            GetProfileByUserIdRequest r when r.UserId == "user-1" => new GetProfileByUserIdResponse { Profile = ProfileFor("user-1") },
            GetProfileByUserIdRequest r => new GetProfileByUserIdResponse { Profile = ProfileFor(r.UserId) },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var (result, _) = await endpoint.AddConversationMember(
            "conv-group", new AddConversationMemberDto { UserId = "stranger" },
            bus, TestPrincipal.ForUser("user-1"), _context, Policy(bus), Blocks(bus));

        // A group must not become a way to put a stranger in front of someone.
        AssertRefusal(result, DmRefusal.RecipientPolicy, StatusCodes.Status403Forbidden, "stranger");
    }

    [Test]
    public async Task AddMember_WhoIsAlreadyIn_IsANoOp()
    {
        await SeedGroupConversation("user-1", "user-2", "user-4");
        var endpoint = new ConversationEndpoints();

        var (result, evt) = await endpoint.AddConversationMember(
            "conv-group", new AddConversationMemberDto { UserId = "user-2" },
            FriendlyBus(), TestPrincipal.ForUser("user-1"), _context, Policy(FriendlyBus()), Blocks(FriendlyBus()));
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.InstanceOf<Ok<ConversationDto>>());
            // No event: a duplicate add must not fire a second join notice at everyone.
            Assert.That(evt, Is.Null);
            Assert.That(await _context.Members.CountAsync(m => m.ConversationId == "conv-group"), Is.EqualTo(3));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ DeleteConversation
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
