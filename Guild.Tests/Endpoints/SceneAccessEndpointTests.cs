using System.Text;
using Guild.Application.Bus.Consumers;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MessagingMessageType = Messaging.Contracts.Bus.Commands.MessageType;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Who plays in a scene: the access preset the server refuses, walking a character in, the ask
/// queue a GM works, and the gate that keeps a closed scene closed.
/// </summary>
[TestFixture]
public class SceneAccessEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string GameMasterId = "user-gm";
    private const string CastPlayerId = "user-cast";
    private const string OutsiderId = "user-outsider";

    private const string ParentChannelId = "chan-ic";
    private const string SceneChannelId = "scene-1";
    private const string OocThreadId = "scene-1-ooc";

    private const string CastPersonaId = "pers_mayor";
    private const string OutsiderPersonaId = "pers_guard";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeInvokingMessageBus _bus = null!;
    private PersonaService _personas = null!;
    private PersonaCastService _cast = null!;
    private SceneVisibilityCache _visibility = null!;
    private GuildPermissionService _permissions = null!;
    private GuildHydrateService _hydrate = null!;
    private SceneService _scenes = null!;
    private SceneJoinService _joins = null!;
    private AuditLogService _auditLog = null!;
    private SceneEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeInvokingMessageBus();
        _personas = new PersonaService(_cache, _context);
        _cast = new PersonaCastService(_context);
        _visibility = new SceneVisibilityCache(_cache, _context, _personas);
        _permissions = new GuildPermissionService(
            _cache, _context, NullLogger<GuildPermissionService>.Instance, null, _visibility);
        _hydrate = new GuildHydrateService(
            RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _scenes = new SceneService(
            _context, new PersonaMentionService(_context, _personas), _cast, _hydrate, _hub);
        _joins = new SceneJoinService(
            _context, _scenes, _visibility, _cast,
            new ModulePermissionHolderService(_context, _permissions), _hub, _bus);
        _auditLog = new AuditLogService(_context);
        _endpoint = new SceneEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════ Seeding
    // ══════════════════════════════════════════════════════════════════════

    private async Task SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Blackwater",
            Features = GuildFeatures.Scenes | GuildFeatures.Personas | GuildFeatures.Threads,
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.Add(new Role
        {
            Id = "role-everyone", GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.ReadMessageHistory
                          | Permissions.SendMessages | Permissions.SendMessagesInThreads,
            ModulePermissions = ModulePermissions.UsePersonas,
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.Add(new Role
        {
            Id = "role-gm", GuildId = GuildId, Name = "Gamemaster",
            ModulePermissions = ModulePermissions.ManageScenes,
            CreatedAt = now, UpdatedAt = now,
        });

        AddMember("memb-gm", GameMasterId);
        AddMember("memb-cast", CastPlayerId);
        AddMember("memb-outsider", OutsiderId);

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-gm-role", RoleId = "role-gm", MemberId = "memb-gm",
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Channels.Add(new Channel
        {
            Id = ParentChannelId, GuildId = GuildId, Name = "the-inn", Type = ChannelType.Text,
            CreatedAt = now, UpdatedAt = now,
        });

        AddPersona(CastPersonaId, "Mayor Cogsgrove", CastPlayerId);
        AddPersona(OutsiderPersonaId, "Town Guard", OutsiderId);

        await _context.SaveChangesAsync();
        return;

        void AddMember(string memberId, string userId)
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
                CreatedAt = now, UpdatedAt = now, SearchValue = $"{userId}#{GuildId}",
            });

            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-{memberId}", RoleId = "role-everyone", MemberId = memberId,
                CreatedAt = now, UpdatedAt = now,
            });
        }

        void AddPersona(string personaId, string name, string ownerUserId)
        {
            _context.Set<Persona>().Add(new Persona
            {
                Id = personaId, Scope = PersonaScope.User, OwnerUserId = ownerUserId, Name = name,
                CreatedAt = now, UpdatedAt = now,
            });

            _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
            {
                Id = $"profile-{personaId}", PersonaId = personaId, GuildId = GuildId,
                ApprovalState = PersonaApprovalState.Approved, CreatedAt = now, UpdatedAt = now,
            });
        }
    }

    private async Task<SceneState> SeedSceneAsync(
        SceneJoinPolicy policy = SceneJoinPolicy.Open,
        SceneVisibility visibility = SceneVisibility.Everyone,
        SceneStatus status = SceneStatus.Active,
        List<string>? cast = null)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Channels.Add(new Channel
        {
            Id = SceneChannelId, GuildId = GuildId, Name = "The Siege of Blackwater",
            Type = ChannelType.Scene, ParentChannelId = ParentChannelId,
            CreatedByUserId = GameMasterId, CreatedAt = now, UpdatedAt = now,
        });

        _context.Channels.Add(new Channel
        {
            Id = OocThreadId, GuildId = GuildId, Name = "The Siege of Blackwater (OOC)",
            Type = ChannelType.Thread, ParentChannelId = ParentChannelId,
            CreatedByUserId = GameMasterId, CreatedAt = now, UpdatedAt = now,
        });

        var state = SceneState.Create(new CreateSceneStateParams
        {
            ChannelId = SceneChannelId, GuildId = GuildId, OocThreadId = OocThreadId,
            ParticipantPersonaIds = cast ?? [CastPersonaId],
            JoinPolicy = policy, Visibility = visibility,
        });

        state.Status = status;

        _context.Set<SceneState>().Add(state);
        await _context.SaveChangesAsync();

        return state;
    }

    // ══════════════════════════════════════════════════════════════════════ The access preset
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Create_DefaultsToAnOpenTableAnybodyCanSee()
    {
        await SeedAsync();

        var result = await CreateAsync(new CreateSceneDto { Name = "The Siege of Blackwater" });
        var dto = (result as Ok<SceneDto>)?.Value;

        Assert.That(dto, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto!.JoinPolicy, Is.EqualTo(SceneJoinPolicy.Open));
            Assert.That(dto.Visibility, Is.EqualTo(SceneVisibility.Everyone));
        });
    }

    [Test]
    public async Task Create_WithACastOnlyOpenTable_IsRefused()
    {
        await SeedAsync();

        var result = await CreateAsync(new CreateSceneDto
        {
            Name = "The Siege of Blackwater",
            JoinPolicy = SceneJoinPolicy.Open,
            Visibility = SceneVisibility.Cast,
        });

        AssertFault(result, "scene_visibility_conflict");
    }

    /// <summary>The pair is judged on where the scene lands, not on what the body named.</summary>
    [Test]
    public async Task Update_ThatOnlyOpensTheTableOnAPrivateScene_IsRefused()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask, SceneVisibility.Cast);

        var result = await UpdateAsync(new UpdateSceneDto { JoinPolicy = SceneJoinPolicy.Open });

        AssertFault(result, "scene_visibility_conflict");
    }

    [Test]
    public async Task Update_TakingAScenePrivate_IsAccepted()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var result = await UpdateAsync(new UpdateSceneDto
        {
            JoinPolicy = SceneJoinPolicy.Ask,
            Visibility = SceneVisibility.Cast,
        });

        var dto = (result as Ok<SceneDto>)?.Value;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Visibility, Is.EqualTo(SceneVisibility.Cast));
    }

    // ══════════════════════════════════════════════════════════════════════ Joining
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Join_PutsTheCharacterInTheCastAndWritesTheLine()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var result = await JoinAsync(OutsiderPersonaId, OutsiderId);
        var dto = (result as Ok<SceneDto>)?.Value;

        Assert.That(dto, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto!.ParticipantPersonaIds, Does.Contain(OutsiderPersonaId));
            Assert.That(SystemMessages(MessagingMessageType.SceneCharacterJoined), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Join_OnAClosedScene_IsRefused()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        AssertFault(await JoinAsync(OutsiderPersonaId, OutsiderId), "scene_not_open");
    }

    [Test]
    public async Task Join_OnAConcludedScene_IsRefused()
    {
        await SeedAsync();
        await SeedSceneAsync(status: SceneStatus.Concluded);

        AssertFault(await JoinAsync(OutsiderPersonaId, OutsiderId), "scene_concluded");
    }

    [Test]
    public async Task Join_WithSomebodyElsesCharacter_IsRefused()
    {
        await SeedAsync();
        await SeedSceneAsync();

        AssertFault(await JoinAsync(CastPersonaId, OutsiderId), "persona_not_usable");
    }

    [Test]
    public async Task Join_Twice_IsRefused()
    {
        await SeedAsync();
        await SeedSceneAsync();

        await JoinAsync(OutsiderPersonaId, OutsiderId);
        await _context.SaveChangesAsync();

        AssertFault(
            await JoinAsync(OutsiderPersonaId, OutsiderId), "persona_already_in_scene");
    }

    [Test]
    public async Task Leave_WritesAPlainLine_WhileAGmRemovalSaysRemoved()
    {
        await SeedAsync();
        await SeedSceneAsync(cast: [CastPersonaId, OutsiderPersonaId]);

        await _endpoint.LeaveAsync(
            GuildId, SceneChannelId, OutsiderPersonaId, _permissions, _scenes, _joins, _personas,
            _context, TestPrincipal.Create(OutsiderId));

        Assert.That(Contents(MessagingMessageType.SceneCharacterLeft), Is.EqualTo(new[] { "" }));

        await _endpoint.RemoveParticipantAsync(
            GuildId, SceneChannelId, CastPersonaId, _permissions, _scenes, _joins, _context,
            TestPrincipal.Create(GameMasterId));

        Assert.That(
            Contents(MessagingMessageType.SceneCharacterLeft),
            Is.EqualTo(new[] { "", SceneJoinService.RemovedContent }));
    }

    // ══════════════════════════════════════════════════════════════════════ Auto-join on a post
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PostingInAnOpenScene_JoinsTheCharacterToIt()
    {
        await SeedAsync();
        var state = await SeedSceneAsync();

        var joined = await _joins.AutoJoinOnPostAsync(
            state, OutsiderPersonaId, OutsiderId, DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(joined, Is.True);
            Assert.That(state.ParticipantPersonaIds, Does.Contain(OutsiderPersonaId));
            Assert.That(SystemMessages(MessagingMessageType.SceneCharacterJoined), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task PostingInAClosedOrConcludedScene_JoinsNobody()
    {
        await SeedAsync();
        var closed = await SeedSceneAsync(SceneJoinPolicy.Ask);

        Assert.That(
            await _joins.AutoJoinOnPostAsync(
                closed, OutsiderPersonaId, OutsiderId, DateTimeOffset.UtcNow),
            Is.False);

        closed.JoinPolicy = SceneJoinPolicy.Open;
        closed.Status = SceneStatus.Concluded;

        Assert.That(
            await _joins.AutoJoinOnPostAsync(
                closed, OutsiderPersonaId, OutsiderId, DateTimeOffset.UtcNow),
            Is.False);
    }

    [Test]
    public async Task APlainMessageInAnOpenScene_JoinsNobody()
    {
        await SeedAsync();
        var state = await SeedSceneAsync();

        Assert.That(
            await _joins.AutoJoinOnPostAsync(state, null, OutsiderId, DateTimeOffset.UtcNow),
            Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════ The ask queue
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Requesting_QueuesTheCharacterAndTellsTheGameMaster()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        var result = await RequestAsync(OutsiderPersonaId, OutsiderId, "I have business at the gate.");
        var dto = (result as Ok<SceneJoinRequestDto>)?.Value;

        Assert.That(dto, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto!.Status, Is.EqualTo(SceneJoinRequestStatus.Pending));
            Assert.That(dto.Note, Is.EqualTo("I have business at the gate."));
            Assert.That(dto.PersonaName, Is.EqualTo("Town Guard"));
            Assert.That(
                ((FakeHubClients)_hub.Clients).RecipientsOf(SceneService.JoinRequestedEvent),
                Does.Contain(GameMasterId));
        });
    }

    [Test]
    public async Task Requesting_OnAnOpenScene_IsRefused()
    {
        await SeedAsync();
        await SeedSceneAsync();

        AssertFault(
            await RequestAsync(OutsiderPersonaId, OutsiderId), "scene_join_not_required");
    }

    [Test]
    public async Task Requesting_Twice_IsRefused()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        await RequestAsync(OutsiderPersonaId, OutsiderId);
        await _context.SaveChangesAsync();

        AssertFault(
            await RequestAsync(OutsiderPersonaId, OutsiderId), "join_request_exists");
    }

    [Test]
    public async Task Denying_KeepsTheReasonAndLetsTheCharacterAskAgain()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        var requestId = await QueueAsync();

        var denied = (await _endpoint.DenyJoinRequestAsync(
                GuildId, SceneChannelId, requestId,
                new DenySceneJoinRequestDto { Reason = "Not this arc." },
                _permissions, _joins, _cast, _context, TestPrincipal.Create(GameMasterId))
            as Ok<SceneJoinRequestDto>)?.Value;

        await _context.SaveChangesAsync();

        Assert.That(denied, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(denied!.Status, Is.EqualTo(SceneJoinRequestStatus.Denied));
            Assert.That(denied.DecisionReason, Is.EqualTo("Not this arc."));
            Assert.That(denied.DecidedByUserId, Is.EqualTo(GameMasterId));
        });

        var again = await RequestAsync(OutsiderPersonaId, OutsiderId);

        Assert.That(again, Is.InstanceOf<Ok<SceneJoinRequestDto>>());
    }

    [Test]
    public async Task Approving_PutsTheCharacterInTheCastAndClosesTheRow()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        var requestId = await QueueAsync();

        var approved = (await _endpoint.ApproveJoinRequestAsync(
                GuildId, SceneChannelId, requestId, _permissions, _scenes, _joins, _cast, _context,
                TestPrincipal.Create(GameMasterId))
            as Ok<SceneJoinRequestDto>)?.Value;

        await _context.SaveChangesAsync();

        var state = await _context.Set<SceneState>().FirstAsync(s => s.ChannelId == SceneChannelId);

        Assert.That(approved, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(approved!.Status, Is.EqualTo(SceneJoinRequestStatus.Approved));
            Assert.That(state.ParticipantPersonaIds, Does.Contain(OutsiderPersonaId));
            Assert.That(SystemMessages(MessagingMessageType.SceneCharacterJoined), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Approving_ARowThatWasAlreadyAnswered_IsRefused()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        var requestId = await QueueAsync();

        await _endpoint.DenyJoinRequestAsync(
            GuildId, SceneChannelId, requestId, new DenySceneJoinRequestDto(),
            _permissions, _joins, _cast, _context, TestPrincipal.Create(GameMasterId));

        await _context.SaveChangesAsync();

        var result = await _endpoint.ApproveJoinRequestAsync(
            GuildId, SceneChannelId, requestId, _permissions, _scenes, _joins, _cast, _context,
            TestPrincipal.Create(GameMasterId));

        AssertFault(result, "join_request_not_pending");
    }

    [Test]
    public async Task Withdrawing_SomebodyElsesRequest_IsRefused()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        var requestId = await QueueAsync();

        var result = await _endpoint.WithdrawJoinRequestAsync(
            GuildId, SceneChannelId, requestId, _permissions, _joins, _cast, _context,
            TestPrincipal.Create(CastPlayerId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    /// <summary>
    /// A player sees that theirs is pending without being told who else asked; the GM sees the
    /// queue.
    /// </summary>
    [Test]
    public async Task TheQueue_ShowsAPlayerOnlyTheirOwnRows()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        await QueueAsync();

        await _context.SceneJoinRequests.AddAsync(SceneJoinRequest.Create(new CreateSceneJoinRequestParams
        {
            SceneChannelId = SceneChannelId, GuildId = GuildId, PersonaId = CastPersonaId,
            RequestedByUserId = CastPlayerId,
        }));

        await _context.SaveChangesAsync();

        var forPlayer = await ListAsync(OutsiderId);
        var forGameMaster = await ListAsync(GameMasterId);

        Assert.Multiple(() =>
        {
            Assert.That(forPlayer.Select(r => r.PersonaId), Is.EqualTo(new[] { OutsiderPersonaId }));
            Assert.That(forGameMaster, Has.Count.EqualTo(2));
        });
    }

    // ══════════════════════════════════════════════════════════════════════ The send gate
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ClosedScene_LetsTheCastSpeakAndRefusesEverybodyElse()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        var castMember = await SendAsync(CastPlayerId, CastPersonaId);
        var outsider = await SendAsync(OutsiderId, OutsiderPersonaId);
        var plain = await SendAsync(OutsiderId, personaId: null);
        var gameMaster = await SendAsync(GameMasterId, personaId: null);

        Assert.Multiple(() =>
        {
            Assert.That(castMember.IsAllowed, Is.True);
            Assert.That(outsider.IsAllowed, Is.False, "a character outside the cast");
            Assert.That(plain.IsAllowed, Is.False, "a plain message is what makes it closed");
            Assert.That(gameMaster.IsAllowed, Is.True, "ManageScenes speaks anywhere");
        });
    }

    [Test]
    public async Task AnOpenScene_LetsAnybodySpeak()
    {
        await SeedAsync();
        await SeedSceneAsync();

        Assert.Multiple(async () =>
        {
            Assert.That((await SendAsync(OutsiderId, OutsiderPersonaId)).IsAllowed, Is.True);
            Assert.That((await SendAsync(OutsiderId, personaId: null)).IsAllowed, Is.True);
        });
    }

    /// <summary>The companion thread follows visibility, never the cast.</summary>
    [Test]
    public async Task TheOutOfCharacterThread_StaysOpenToEverybodyWhoCanSeeTheScene()
    {
        await SeedAsync();
        await SeedSceneAsync(SceneJoinPolicy.Ask);

        Assert.Multiple(async () =>
        {
            Assert.That(
                (await SendAsync(CastPlayerId, CastPersonaId, OocThreadId)).IsAllowed, Is.True);
            Assert.That(
                (await SendAsync(OutsiderId, OutsiderPersonaId, OocThreadId)).IsAllowed, Is.True);
            Assert.That((await SendAsync(OutsiderId, null, OocThreadId)).IsAllowed, Is.True);
            Assert.That((await SendAsync(GameMasterId, null, OocThreadId)).IsAllowed, Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Helpers
    // ══════════════════════════════════════════════════════════════════════

    private Task<IResult> CreateAsync(CreateSceneDto dto) =>
        _endpoint.CreateAsync(GuildId, ParentChannelId, dto, _permissions, _scenes, _auditLog,
            _hydrate, _hub, _bus, _visibility, _context, TestPrincipal.Create(GameMasterId));

    private Task<IResult> UpdateAsync(UpdateSceneDto dto) =>
        _endpoint.UpdateAsync(GuildId, SceneChannelId, dto, _permissions, _scenes, _joins,
            _visibility, _auditLog, _context, TestPrincipal.Create(GameMasterId));

    private Task<IResult> JoinAsync(string personaId, string userId) =>
        _endpoint.JoinAsync(GuildId, SceneChannelId, new JoinSceneDto { PersonaId = personaId },
            _permissions, _scenes, _joins, _personas, _context, TestPrincipal.Create(userId));

    private Task<IResult> RequestAsync(string personaId, string userId, string? note = null) =>
        _endpoint.RequestJoinAsync(GuildId, SceneChannelId,
            new CreateSceneJoinRequestDto { PersonaId = personaId, Note = note },
            _permissions, _scenes, _joins, _personas, _cast, _context, TestPrincipal.Create(userId));

    /// <summary>One pending row from the outsider, committed, with its id.</summary>
    private async Task<string> QueueAsync()
    {
        var queued = (await RequestAsync(OutsiderPersonaId, OutsiderId) as Ok<SceneJoinRequestDto>)?.Value;
        await _context.SaveChangesAsync();

        Assert.That(queued, Is.Not.Null);
        return queued!.Id;
    }

    private async Task<IReadOnlyList<SceneJoinRequestDto>> ListAsync(string userId)
    {
        var result = await _endpoint.ListJoinRequestsAsync(
            GuildId, SceneChannelId, _permissions, _cast, _context, TestPrincipal.Create(userId));

        return (result as Ok<SceneJoinRequestListDto>)?.Value?.Requests ?? [];
    }

    private Task<Guild.Contracts.Bus.Response.ResolvePersonaForSendResponse> SendAsync(
        string userId, string? personaId, string channelId = SceneChannelId) =>
        ResolvePersonaForSendHandler.Handle(
            new ResolvePersonaForSendRequest
            {
                UserId = userId,
                ChannelId = channelId,
                PersonaId = personaId,
                Content = "The gate holds.",
            },
            _personas, _permissions,
            RoleplayTestFactory.CreateRealtime(_context, _permissions, _personas, _hub), _context);

    private List<CreateMessageCommand> SystemMessages(MessagingMessageType type) =>
        _bus.Invoked.OfType<CreateMessageCommand>().Where(c => c.Type == type).ToList();

    private string[] Contents(MessagingMessageType type) =>
        SystemMessages(type).Select(c => Encoding.UTF8.GetString(c.Content)).ToArray();

    /// <summary>Reads the `error` off a Fault, which is Results.Json over an anonymous type.</summary>
    private static void AssertFault(IResult result, string error)
    {
        var value = result.GetType().GetProperty("Value")?.GetValue(result);
        var code = value?.GetType().GetProperty("error")?.GetValue(value) as string;

        Assert.That(code, Is.EqualTo(error));
    }
}
