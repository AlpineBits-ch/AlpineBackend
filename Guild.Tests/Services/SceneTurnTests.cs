using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// Scenes: the turn moving on its own when the right character posts, absences stepping the turn
/// over somebody on holiday, and the stale-turn sweep reaching the GM on the second miss.
/// </summary>
[TestFixture]
public class SceneTurnTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string ChannelId = "chan-1";
    private const string GameMasterId = "user-gm";
    private const string PlayerId = "user-player";
    private const string OtherPlayerId = "user-other";

    private const string GmPersonaId = "pers_narrator";
    private const string PlayerPersonaId = "pers_mayor";
    private const string OtherPersonaId = "pers_guard";

    private const string GuildPersonaId = "pers_chorus";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeInvokingMessageBus _bus = null!;
    private GuildPermissionService _permissions = null!;
    private GuildHydrateService _hydrate = null!;
    private PersonaService _personas = null!;
    private PersonaCastService _cast = null!;
    private SceneService _scenes = null!;
    private SceneEndpoint _endpoint = null!;
    private AuditLogService _auditLog = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeInvokingMessageBus();
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _hydrate = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _personas = new PersonaService(_cache, _context);
        _cast = new PersonaCastService(_context);
        _scenes = new SceneService(
            _context, new PersonaMentionService(_context, _personas), _cast, _hydrate, _hub);
        _endpoint = new SceneEndpoint();
        _auditLog = new AuditLogService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════ Seeding
    // ══════════════════════════════════════════════════════════════════════

    private async Task SeedAsync(GuildFeatures features = GuildFeatures.Scenes | GuildFeatures.Personas
        | GuildFeatures.Threads | GuildFeatures.Presence)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Blackwater", Features = features,
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.Add(new Role
        {
            Id = "role-everyone", GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages | Permissions.SendMessagesInThreads
                          | Permissions.ManageOwnThreads,
            CreatedAt = now, UpdatedAt = now,
        });

        // Only the GM holds ManageScenes, so the escalation audience is a real subset rather than
        // everybody in the fixture.
        _context.Roles.Add(new Role
        {
            Id = "role-gm", GuildId = GuildId, Name = "Gamemaster",
            ModulePermissions = ModulePermissions.ManageScenes,
            CreatedAt = now, UpdatedAt = now,
        });

        AddMember("memb-gm", GameMasterId);
        AddMember("memb-player", PlayerId);
        AddMember("memb-other", OtherPlayerId);

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-gm-role", RoleId = "role-gm", MemberId = "memb-gm",
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "the-inn", Type = ChannelType.Text,
            CreatedAt = now, UpdatedAt = now,
        });

        AddPersona(GmPersonaId, "Narrator", GameMasterId);
        AddPersona(PlayerPersonaId, "Mayor Cogsgrove", PlayerId);
        AddPersona(OtherPersonaId, "Town Guard", OtherPlayerId);

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

    /// <summary>An active scene with the three characters in rotation and the turn on the Mayor.</summary>
    private async Task<SceneState> SeedSceneAsync(
        string? currentTurn = PlayerPersonaId, DateTimeOffset? deadline = null,
        string sceneChannelId = "scene-1", List<string>? cast = null, bool archived = false)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Channels.Add(new Channel
        {
            Id = sceneChannelId, GuildId = GuildId, Name = "The Siege of Blackwater", Type = ChannelType.Scene,
            ParentChannelId = ChannelId, CreatedByUserId = GameMasterId, IsArchived = archived,
            CreatedAt = now, UpdatedAt = now,
        });

        var state = SceneState.Create(new CreateSceneStateParams
        {
            ChannelId = sceneChannelId, GuildId = GuildId, TurnLengthHours = 48,
            ParticipantPersonaIds = cast ?? [PlayerPersonaId, OtherPersonaId, GmPersonaId],
        });

        state.Status = SceneStatus.Active;
        state.CurrentTurnPersonaId = currentTurn;
        state.TurnDeadlineAt = deadline;

        _context.Set<SceneState>().Add(state);
        await _context.SaveChangesAsync();

        return state;
    }

    /// <summary>A guild-owned character the given player holds a grant on.</summary>
    private async Task SeedGrantedGuildPersonaAsync(string userId)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Set<Persona>().Add(new Persona
        {
            Id = GuildPersonaId, Scope = PersonaScope.Guild, OwnerGuildId = GuildId, Name = "The Chorus",
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"profile-{GuildPersonaId}", PersonaId = GuildPersonaId, GuildId = GuildId,
            ApprovalState = PersonaApprovalState.Approved, CreatedAt = now, UpdatedAt = now,
        });

        _context.Set<PersonaGrant>().Add(new PersonaGrant
        {
            Id = "grant-chorus", PersonaId = GuildPersonaId, UserId = userId,
            CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
        await _personas.InvalidateGuildAsync(GuildId);
    }

    private async Task AbsentAsync(string userId, DateTimeOffset from, DateTimeOffset to)
    {
        _context.MemberAbsences.Add(MemberAbsence.Create(new CreateMemberAbsenceParams
        {
            GuildId = GuildId, UserId = userId, StartAt = from, EndAt = to, CreatedByUserId = userId,
        }));

        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════ Creation
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Create_AcceptsANameWithSpaces_AndOpensTheOocCompanion()
    {
        await SeedAsync();

        var result = await CreateAsync(new CreateSceneDto
        {
            Name = "The Siege of Blackwater",
            ParticipantPersonaIds = [PlayerPersonaId, OtherPersonaId],
            TurnLengthHours = 48,
        });

        await _context.SaveChangesAsync();

        var dto = (result as Ok<SceneDto>)?.Value;
        Assert.That(dto, Is.Not.Null);

        var scene = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == dto!.ChannelId);
        Assert.Multiple(() =>
        {
            Assert.That(scene.Name, Is.EqualTo("The Siege of Blackwater"));
            Assert.That(scene.Type, Is.EqualTo(ChannelType.Scene));
            Assert.That(scene.ParentChannelId, Is.EqualTo(ChannelId));
            Assert.That(scene.CreatedByUserId, Is.EqualTo(GameMasterId));
            Assert.That(dto!.OocThreadId, Is.Not.Null);
            Assert.That(dto.Status, Is.EqualTo(SceneStatus.Open));
        });

        var ooc = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == dto!.OocThreadId);
        Assert.Multiple(() =>
        {
            Assert.That(ooc.Type, Is.EqualTo(ChannelType.Thread));
            Assert.That(ooc.ParentChannelId, Is.EqualTo(ChannelId));
            Assert.That(ooc.Name, Is.EqualTo("The Siege of Blackwater (OOC)"));
        });
    }

    [Test]
    public async Task Create_WithoutTheScenesModule_IsForbidden()
    {
        await SeedAsync(GuildFeatures.Personas | GuildFeatures.Threads);

        var result = await CreateAsync(new CreateSceneDto { Name = "The Siege of Blackwater" });

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Create_WithoutManageScenes_IsForbidden()
    {
        await SeedAsync();

        var result = await CreateAsync(
            new CreateSceneDto { Name = "The Siege of Blackwater" }, PlayerId);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Create_WithAnUnadoptedPersona_IsRefused()
    {
        await SeedAsync();

        var result = await CreateAsync(new CreateSceneDto
        {
            Name = "The Siege of Blackwater", ParticipantPersonaIds = ["pers_nobody"],
        });

        Assert.Multiple(() =>
        {
            Assert.That((result as IStatusCodeHttpResult)?.StatusCode,
                Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(FaultCode(result), Is.EqualTo("persona_not_adopted"));
        });
    }

    [Test]
    public async Task Create_WithOnlyATurnOrder_TakesTheCastFromIt()
    {
        await SeedAsync();

        var result = await CreateAsync(new CreateSceneDto
        {
            Name = "The Siege of Blackwater",
            TurnOrder = [PlayerPersonaId, OtherPersonaId],
            TurnLengthHours = 48,
        });

        var dto = (result as Ok<SceneDto>)?.Value;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.ParticipantPersonaIds, Is.EqualTo(new[] { PlayerPersonaId, OtherPersonaId }));
    }

    [Test]
    public async Task Create_Active_OpensTheFirstTurn()
    {
        await SeedAsync();

        var result = await CreateAsync(new CreateSceneDto
        {
            Name = "The Siege of Blackwater",
            TurnOrder = [PlayerPersonaId, OtherPersonaId],
            TurnLengthHours = 48,
            Status = SceneStatus.Active,
        });

        var dto = (result as Ok<SceneDto>)?.Value;
        Assert.That(dto, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto!.Status, Is.EqualTo(SceneStatus.Active));
            Assert.That(dto.CurrentTurnPersonaId, Is.EqualTo(PlayerPersonaId));
            Assert.That(dto.TurnDeadlineAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Create_Concluded_IsRefused()
    {
        await SeedAsync();

        var result = await CreateAsync(new CreateSceneDto
        {
            Name = "The Siege of Blackwater", Status = SceneStatus.Concluded,
        });

        Assert.That(FaultCode(result), Is.EqualTo("scene_status_not_openable"));
    }

    // ══════════════════════════════════════════════════════════════════════ Turn on post
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Turn_AdvancesWhenTheCharacterWhoseTurnItIsPosts()
    {
        await SeedAsync();
        var state = await SeedSceneAsync();

        var moved = await _scenes.AdvanceOnPostAsync(state, PlayerPersonaId, DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True);
            Assert.That(state.CurrentTurnPersonaId, Is.EqualTo(OtherPersonaId));
            Assert.That(state.TurnDeadlineAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Turn_DoesNotAdvanceWhenSomebodyElsePosts()
    {
        await SeedAsync();
        var state = await SeedSceneAsync();

        var moved = await _scenes.AdvanceOnPostAsync(state, GmPersonaId, DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.False);
            Assert.That(state.CurrentTurnPersonaId, Is.EqualTo(PlayerPersonaId));
        });
    }

    [Test]
    public async Task Turn_DoesNotAdvanceOnAPostWithNoCharacterOnIt()
    {
        await SeedAsync();
        var state = await SeedSceneAsync();

        Assert.That(await _scenes.AdvanceOnPostAsync(state, null, DateTimeOffset.UtcNow), Is.False);
    }

    [Test]
    public async Task Turn_DoesNotAdvanceWhileTheSceneIsPaused()
    {
        await SeedAsync();
        var state = await SeedSceneAsync();
        state.Status = SceneStatus.Paused;

        Assert.That(await _scenes.AdvanceOnPostAsync(state, PlayerPersonaId, DateTimeOffset.UtcNow), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════ Absence
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Turn_StepsOverAPlayerWhoDeclaredAnAbsence()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        var state = await SeedSceneAsync();

        await AbsentAsync(OtherPlayerId, now.AddDays(-1), now.AddDays(3));

        await _scenes.AdvanceOnPostAsync(state, PlayerPersonaId, now);

        // The Guard is next in rotation and away, so the turn lands on the Narrator instead.
        Assert.That(state.CurrentTurnPersonaId, Is.EqualTo(GmPersonaId));
    }

    [Test]
    public async Task Turn_IgnoresAnAbsenceThatHasAlreadyEnded()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        var state = await SeedSceneAsync();

        await AbsentAsync(OtherPlayerId, now.AddDays(-5), now.AddDays(-1));

        await _scenes.AdvanceOnPostAsync(state, PlayerPersonaId, now);

        Assert.That(state.CurrentTurnPersonaId, Is.EqualTo(OtherPersonaId));
    }

    [Test]
    public async Task Turn_GoesToNobodyWhenEveryPlayerIsAway()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        var state = await SeedSceneAsync();

        foreach (var userId in new[] { GameMasterId, PlayerId, OtherPlayerId })
            await AbsentAsync(userId, now.AddDays(-1), now.AddDays(3));

        await _scenes.AdvanceOnPostAsync(state, PlayerPersonaId, now);

        Assert.Multiple(() =>
        {
            Assert.That(state.CurrentTurnPersonaId, Is.Null);
            Assert.That(state.TurnDeadlineAt, Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ The nudge
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Nudge_ReachesThePlayerFirstAndTheGameMasterOnTheSecondMiss()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedSceneAsync(deadline: now.AddHours(-1));

        var first = await BuildNudges().SendDueNudgesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first.Nudged, Is.EqualTo(1));
            Assert.That(first.Escalated, Is.Zero);
        });

        var chased = Nudged();
        Assert.Multiple(() =>
        {
            Assert.That(chased, Does.Contain(PlayerId));
            Assert.That(chased, Does.Not.Contain(GameMasterId));
        });

        // Rewound rather than waited out: the grace is a day, and the second miss is the point.
        var state = await _context.Set<SceneState>().FirstAsync();
        state.LastNudgedAt = now.AddHours(-25);
        await _context.SaveChangesAsync();

        ((FakeHubClients)_hub.Clients).SentToUsers.Clear();

        var second = await BuildNudges().SendDueNudgesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second.Nudged, Is.EqualTo(1));
            Assert.That(second.Escalated, Is.EqualTo(1));
            Assert.That(Nudged(), Does.Contain(GameMasterId));
            Assert.That(Nudged(), Does.Contain(PlayerId));
        });
    }

    [Test]
    public async Task Nudge_SkipsTheTurnInsteadOfChasingSomebodyOnHoliday()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedSceneAsync(deadline: now.AddHours(-1));

        await AbsentAsync(PlayerId, now.AddDays(-1), now.AddDays(3));

        var outcome = await BuildNudges().SendDueNudgesAsync();

        var state = await _context.Set<SceneState>().AsNoTracking().FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Skipped, Is.EqualTo(1));
            Assert.That(outcome.Nudged, Is.Zero);
            Assert.That(state.CurrentTurnPersonaId, Is.EqualTo(OtherPersonaId));
            Assert.That(Nudged(), Is.Empty);
        });
    }

    [Test]
    public async Task Nudge_LeavesATurnThatIsNotYetOverdueAlone()
    {
        await SeedAsync();
        await SeedSceneAsync(deadline: DateTimeOffset.UtcNow.AddHours(4));

        var outcome = await BuildNudges().SendDueNudgesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Nudged, Is.Zero);
            Assert.That(outcome.Skipped, Is.Zero);
        });
    }

    [Test]
    public async Task Nudge_LeavesAPausedSceneAlone()
    {
        await SeedAsync();
        var state = await SeedSceneAsync(deadline: DateTimeOffset.UtcNow.AddHours(-1));
        state.Status = SceneStatus.Paused;
        await _context.SaveChangesAsync();

        Assert.That((await BuildNudges().SendDueNudgesAsync()).Nudged, Is.Zero);
    }

    // ══════════════════════════════════════════════════════════════════════ Thread shape
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Scene_ShowsUpInTheThreadListOfItsParent()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var threads = new ThreadEndpoint();
        var result = await threads.GetThreadsAsync(
            ChannelId, _permissions, _context, TestPrincipal.Create(PlayerId));

        var listed = (result as Ok<List<ChannelDto>>)?.Value;

        Assert.That(listed, Is.Not.Null);
        Assert.That(listed!.Select(c => c.Id), Does.Contain("scene-1"));
    }

    [Test]
    public async Task Scene_ArchivesThroughTheThreadRoute()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var threads = new ThreadEndpoint();
        var result = await threads.ArchiveThreadAsync(
            "scene-1", _permissions, _context, _auditLog, _hub, _hydrate, _bus,
            TestPrincipal.Create(GameMasterId));

        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That((await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == "scene-1")).IsArchived, Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ Turn routes
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Advance_IsOpenToWhoeverAnswersForTheCharacterWhoseTurnItIs()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var result = await _endpoint.AdvanceTurnAsync(
            GuildId, "scene-1", _permissions, _scenes, _context, TestPrincipal.Create(PlayerId));

        var dto = (result as Ok<SceneDto>)?.Value;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.CurrentTurnPersonaId, Is.EqualTo(OtherPersonaId));
    }

    [Test]
    public async Task Advance_IsRefusedToABystander()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var result = await _endpoint.AdvanceTurnAsync(
            GuildId, "scene-1", _permissions, _scenes, _context, TestPrincipal.Create(OtherPlayerId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Skip_IsGameMasterOnly()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var refused = await _endpoint.SkipTurnAsync(
            GuildId, "scene-1", _permissions, _scenes, _context, TestPrincipal.Create(PlayerId));

        var allowed = await _endpoint.SkipTurnAsync(
            GuildId, "scene-1", _permissions, _scenes, _context, TestPrincipal.Create(GameMasterId));

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(allowed, Is.InstanceOf<Ok<SceneDto>>());
        });
    }

    [Test]
    public async Task RemovingTheCharacterWhoseTurnItIsHandsTheTurnOn()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var result = await _endpoint.RemoveParticipantAsync(
            GuildId, "scene-1", PlayerPersonaId, _permissions, _scenes, _context,
            TestPrincipal.Create(GameMasterId));

        var dto = (result as Ok<SceneDto>)?.Value;

        Assert.That(dto, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto!.ParticipantPersonaIds, Does.Not.Contain(PlayerPersonaId));
            Assert.That(dto.CurrentTurnPersonaId, Is.EqualTo(OtherPersonaId));
        });
    }

    // ══════════════════════════════════════════════════════════════════════ The list
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task List_WaitingOnMe_ReturnsOnlyTheScenesWhoseTurnIsMine()
    {
        await SeedAsync();
        await SeedSceneAsync();
        await SeedSceneAsync(currentTurn: OtherPersonaId, sceneChannelId: "scene-2");

        var mine = await ListAsync(PlayerId, waitingOnMe: true);
        var all = await ListAsync(PlayerId);

        Assert.Multiple(() =>
        {
            Assert.That(mine!.Scenes.Select(s => s.ChannelId), Is.EqualTo(new[] { "scene-1" }).AsCollection);
            Assert.That(mine.Scenes[0].IsWaitingOnMe, Is.True);
            Assert.That(all!.Scenes.Select(s => s.ChannelId), Is.EquivalentTo(new[] { "scene-1", "scene-2" }));
            Assert.That(all.Scenes.Single(s => s.ChannelId == "scene-2").IsWaitingOnMe, Is.False);
        });
    }

    [Test]
    public async Task List_CarriesEnoughToRenderARowWithoutASecondCall()
    {
        await SeedAsync();
        var deadline = DateTimeOffset.UtcNow.AddHours(6);
        await SeedSceneAsync(deadline: deadline);

        var row = (await ListAsync(PlayerId))!.Scenes.Single();

        Assert.Multiple(() =>
        {
            Assert.That(row.Name, Is.EqualTo("The Siege of Blackwater"));
            Assert.That(row.Status, Is.EqualTo(SceneStatus.Active));
            Assert.That(row.CurrentTurnPersonaId, Is.EqualTo(PlayerPersonaId));
            Assert.That(row.TurnDeadlineAt, Is.EqualTo(deadline));
            Assert.That(row.ParticipantCount, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task List_RevokingAGrantTakesTheSceneOutOfWaitingOnMe()
    {
        await SeedAsync();
        await SeedGrantedGuildPersonaAsync(PlayerId);
        await SeedSceneAsync(currentTurn: GuildPersonaId, cast: [GuildPersonaId, PlayerPersonaId]);

        var before = await ListAsync(PlayerId, waitingOnMe: true);

        _context.Set<PersonaGrant>().RemoveRange(_context.Set<PersonaGrant>());
        await _context.SaveChangesAsync();
        await _personas.InvalidateGuildAsync(GuildId);

        var after = await ListAsync(PlayerId, waitingOnMe: true);

        Assert.Multiple(() =>
        {
            Assert.That(before!.Scenes, Has.Count.EqualTo(1));
            Assert.That(after!.Scenes, Is.Empty, "the shared character is no longer the caller's to speak as");
        });
    }

    [Test]
    public async Task List_LeavesOutArchivedAndConcludedScenesUnlessAsked()
    {
        await SeedAsync();
        await SeedSceneAsync(sceneChannelId: "scene-archived", archived: true);

        var concluded = await SeedSceneAsync(sceneChannelId: "scene-concluded");
        concluded.Status = SceneStatus.Concluded;
        await _context.SaveChangesAsync();

        var listed = await ListAsync(PlayerId);
        var everything = await ListAsync(PlayerId, includeConcluded: true, includeArchived: true);

        Assert.Multiple(() =>
        {
            Assert.That(listed!.Scenes, Is.Empty);
            Assert.That(everything!.Scenes.Select(s => s.ChannelId),
                Is.EquivalentTo(new[] { "scene-archived", "scene-concluded" }));
        });
    }

    [Test]
    public async Task List_WithoutTheScenesModule_IsForbidden()
    {
        await SeedAsync(GuildFeatures.Personas | GuildFeatures.Threads);
        await SeedSceneAsync();

        var result = await _endpoint.ListAsync(
            GuildId, _permissions, _personas, _cast, _context, TestPrincipal.Create(PlayerId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task List_IsForbiddenToSomebodyWhoIsNotInTheGuild()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var result = await _endpoint.ListAsync(
            GuildId, _permissions, _personas, _cast, _context, TestPrincipal.Create("user-stranger"));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public void List_TheQueryCompilesToSqlOnTheRealProvider()
    {
        // InMemory cannot fail on LINQ Npgsql would refuse, and this query joins, orders on a null
        // test and counts a Postgres array column.
        using var postgres = new PostgresGuildContext();

        Assert.Multiple(() =>
        {
            Assert.That(
                SceneEndpoint.BuildListQuery(postgres, GuildId, null, false, false).ToQueryString(),
                Does.Contain("SELECT"));

            Assert.That(
                SceneEndpoint.BuildListQuery(postgres, GuildId, [PlayerPersonaId], false, false).ToQueryString(),
                Does.Contain("SELECT"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Absence on the wire
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Scene_NamesTheCharactersItIsSteppingOverAndNeverTheirPlayers()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedSceneAsync();
        await AbsentAsync(OtherPlayerId, now.AddDays(-1), now.AddDays(3));

        var result = await _endpoint.GetAsync(
            GuildId, "scene-1", _permissions, _scenes, _context, TestPrincipal.Create(PlayerId));

        var dto = (result as Ok<SceneDto>)?.Value;
        var serialized = System.Text.Json.JsonSerializer.Serialize(dto);

        Assert.That(dto, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto!.AwayPersonaIds, Is.EqualTo(new[] { OtherPersonaId }).AsCollection);
            Assert.That(serialized, Does.Not.Contain(OtherPlayerId),
                "who plays the Guard is exactly what an away flag must not disclose");
        });
    }

    [Test]
    public async Task Scene_DoesNotCallACharacterNobodyAnswersForAway()
    {
        await SeedAsync();
        await SeedSceneAsync(cast: [PlayerPersonaId, "pers_orphan"]);

        var away = await _scenes.AwayPersonasAsync(
            GuildId, ["pers_orphan"], DateTimeOffset.UtcNow);

        var unavailable = await _scenes.UnavailablePersonasAsync(
            GuildId, ["pers_orphan"], DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(away, Is.Empty, "unplayable is not the same statement as on holiday");
            Assert.That(unavailable, Does.Contain("pers_orphan"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════ The cast on the wire
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Scene_CarriesTheCastsDisplayDataSoOtherPlayersCharactersCanBeDrawn()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var result = await _endpoint.GetAsync(
            GuildId, "scene-1", _permissions, _scenes, _context, TestPrincipal.Create(PlayerId));

        var dto = (result as Ok<SceneDto>)?.Value;

        Assert.That(dto, Is.Not.Null);
        var guard = dto!.Participants.Single(p => p.PersonaId == OtherPersonaId);

        Assert.Multiple(() =>
        {
            Assert.That(dto.Participants.Select(p => p.PersonaId),
                Is.EqualTo(dto.ParticipantPersonaIds).AsCollection);
            Assert.That(guard.Name, Is.EqualTo("Town Guard"),
                "a scene renders other players' characters, which nothing else in the API names");
            Assert.That(dto.Participants.Single(p => p.PersonaId == PlayerPersonaId).IsCurrentTurn, Is.True);
        });
    }

    [Test]
    public async Task List_NamesTheCharacterOnTheClock()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var row = (await ListAsync(OtherPlayerId))!.Scenes.Single();

        Assert.That(row.CurrentTurnName, Is.EqualTo("Mayor Cogsgrove"));
    }

    // ══════════════════════════════════════════════════════════════════════ The clock
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Turn_RecordsWhenItOpenedAndWhichTurnItIs()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        var state = await SeedSceneAsync();

        await _scenes.AdvanceOnPostAsync(state, PlayerPersonaId, now);

        Assert.Multiple(() =>
        {
            Assert.That(state.TurnStartedAt, Is.EqualTo(now));
            Assert.That(state.TurnNumber, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Turn_MovingTellsTheRoomWhenItOpenedAndWhenItIsDue()
    {
        await SeedAsync();
        var state = await SeedSceneAsync();

        // The fan-out is addressed to whoever is present in the guild, as every other guild event
        // is, so there has to be somebody there to receive it.
        var watched = new SceneService(
            _context, new PersonaMentionService(_context, _personas), _cast,
            new GuildHydrateService(
                RedisTestFactory.CreateWithPresence(new MemberPresenceState
                {
                    MemberId = "memb-other", UserId = OtherPlayerId, Status = "Online",
                }),
                NullLogger<GuildHydrateService>.Instance),
            _hub);

        await watched.AdvanceOnPostAsync(state, PlayerPersonaId, DateTimeOffset.UtcNow);

        var sent = ((FakeHubClients)_hub.Clients).SentMessages
            .Where(s => s.Method == SceneService.TurnChangedEvent)
            .ToList();

        Assert.That(sent, Has.Count.EqualTo(1), "every participant's rail is stale without this");

        var payload = System.Text.Json.JsonSerializer.Serialize(sent[0].Args[0]);

        Assert.Multiple(() =>
        {
            Assert.That(payload, Does.Contain("TurnStartedAt"));
            Assert.That(payload, Does.Contain("TurnNumber"));
            Assert.That(payload, Does.Contain(OtherPersonaId));
        });
    }

    [Test]
    public async Task Starting_AScene_OpensTheFirstTurnOnTheClock()
    {
        await SeedAsync();
        var state = await SeedSceneAsync(currentTurn: null);
        state.Status = SceneStatus.Open;
        await _context.SaveChangesAsync();

        var result = await _endpoint.UpdateAsync(
            GuildId, "scene-1", new UpdateSceneDto { Status = SceneStatus.Active },
            _permissions, _scenes, _auditLog, _context, TestPrincipal.Create(GameMasterId));

        var dto = (result as Ok<SceneDto>)?.Value;

        Assert.That(dto, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto!.CurrentTurnPersonaId, Is.EqualTo(PlayerPersonaId));
            Assert.That(dto.TurnStartedAt, Is.Not.Null);
            Assert.That(dto.TurnNumber, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Concluding_ASceneKeepsTheClosingLine()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var result = await _endpoint.UpdateAsync(
            GuildId, "scene-1",
            new UpdateSceneDto { Status = SceneStatus.Concluded, ConclusionNote = "The siege broke at dawn." },
            _permissions, _scenes, _auditLog, _context, TestPrincipal.Create(GameMasterId));

        var dto = (result as Ok<SceneDto>)?.Value;

        Assert.That(dto?.ConclusionNote, Is.EqualTo("The siege broke at dawn."));
    }

    [Test]
    public async Task Creating_AScene_AnnouncesItAsAGameRatherThanOnlyAsTwoThreads()
    {
        await SeedAsync();

        await _endpoint.CreateAsync(GuildId, ChannelId,
            new CreateSceneDto
            {
                Name = "The Siege of Blackwater", TurnLengthHours = 48,
                TurnOrder = [PlayerPersonaId, OtherPersonaId],
            },
            _permissions, Watched(), _auditLog, _hydrate, _hub, _bus, _context,
            TestPrincipal.Create(GameMasterId));

        var sent = ((FakeHubClients)_hub.Clients).SentMessages
            .Where(s => s.Method == SceneService.CreatedEvent)
            .ToList();

        Assert.That(sent, Has.Count.EqualTo(1));

        var payload = System.Text.Json.JsonSerializer.Serialize(sent[0].Args[0]);

        Assert.Multiple(() =>
        {
            Assert.That(payload, Does.Contain("OocThreadId"));
            Assert.That(payload, Does.Contain(PlayerPersonaId));
            Assert.That(payload, Does.Contain("The Siege of Blackwater"));
        });
    }

    [Test]
    public async Task Concluding_AScene_AnnouncesTheEndingOnce()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var watched = Watched();

        await _endpoint.UpdateAsync(GuildId, "scene-1",
            new UpdateSceneDto { Status = SceneStatus.Concluded, ConclusionNote = "The siege broke at dawn." },
            _permissions, watched, _auditLog, _context, TestPrincipal.Create(GameMasterId));

        // A second PATCH on an already concluded scene edits a chronicle rather than ending it
        // again.
        await _endpoint.UpdateAsync(GuildId, "scene-1",
            new UpdateSceneDto { ConclusionNote = "The siege broke at first light." },
            _permissions, watched, _auditLog, _context, TestPrincipal.Create(GameMasterId));

        var sent = ((FakeHubClients)_hub.Clients).SentMessages
            .Where(s => s.Method == SceneService.ConcludedEvent)
            .ToList();

        Assert.That(sent, Has.Count.EqualTo(1));
        Assert.That(
            System.Text.Json.JsonSerializer.Serialize(sent[0].Args[0]),
            Does.Contain("The siege broke at dawn."));
    }

    /// <summary>A scene service whose fan-out has somebody present to reach.</summary>
    private SceneService Watched() =>
        new(_context, new PersonaMentionService(_context, _personas), _cast,
            new GuildHydrateService(
                RedisTestFactory.CreateWithPresence(new MemberPresenceState
                {
                    MemberId = "memb-other", UserId = OtherPlayerId, Status = "Online",
                }),
                NullLogger<GuildHydrateService>.Instance),
            _hub);

    // ══════════════════════════════════════════════════════════════════════ The nudge push
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ManualNudge_ChasesTheTurnWithoutWaitingForTheSweep()
    {
        await SeedAsync();
        // No deadline at all, so the sweep would never pick this scene up.
        await SeedSceneAsync();

        var result = await _endpoint.NudgeTurnAsync(
            GuildId, "scene-1", _permissions, _scenes, BuildNudges(), _context,
            TestPrincipal.Create(GameMasterId));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<SceneDto>>());
            Assert.That(Nudged(), Does.Contain(PlayerId));
            Assert.That(_bus.Published.OfType<SceneTurnPushRequested>().Single().SceneName,
                Is.EqualTo("The Siege of Blackwater"),
                "a nudge for a game somebody had forgotten has to name the game");
        });
    }

    [Test]
    public async Task ManualNudge_IsGameMasterOnly()
    {
        await SeedAsync();
        await SeedSceneAsync();

        var result = await _endpoint.NudgeTurnAsync(
            GuildId, "scene-1", _permissions, _scenes, BuildNudges(), _context,
            TestPrincipal.Create(PlayerId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Nudge_PushesUnderTheCharactersNameAndNotTheAccount()
    {
        await SeedAsync();
        await SeedSceneAsync(deadline: DateTimeOffset.UtcNow.AddHours(-1));

        await BuildNudges().SendDueNudgesAsync();

        var push = _bus.Published.OfType<SceneTurnPushRequested>().Single();
        var serialized = System.Text.Json.JsonSerializer.Serialize(
            new { push.PersonaId, push.AuthorDisplayName, push.SceneName, push.PersonaHidden });

        Assert.Multiple(() =>
        {
            Assert.That(push.UserIds, Is.EqualTo(new[] { PlayerId }).AsCollection);
            Assert.That(push.PersonaId, Is.EqualTo(PlayerPersonaId));
            Assert.That(push.AuthorDisplayName, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(push.PersonaHidden, Is.False);
            Assert.That(push.Escalated, Is.False);
            Assert.That(serialized, Does.Not.Contain(PlayerId),
                "the payload names the character; the account is only ever an address");
        });
    }

    [Test]
    public async Task Nudge_MasksACharacterItCannotName()
    {
        await SeedAsync();
        await SeedSceneAsync(deadline: DateTimeOffset.UtcNow.AddHours(-1));

        // Un-adopted here, so there is no per-guild name to send. The push masks rather than
        // reaching for the account behind the character.
        var named = await _cast.ResolveAsync(GuildId, ["pers_stranger"]);

        Assert.That(named.ContainsKey("pers_stranger"), Is.False);
    }

    [Test]
    public async Task Nudge_EscalatesToTheGameMasterUnderItsOwnCopy()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        var state = await SeedSceneAsync(deadline: now.AddHours(-1));

        state.NudgeCount = 1;
        state.LastNudgedAt = now.AddHours(-25);
        await _context.SaveChangesAsync();

        await BuildNudges().SendDueNudgesAsync();

        var pushes = _bus.Published.OfType<SceneTurnPushRequested>().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(pushes.Single(p => !p.Escalated).UserIds, Is.EqualTo(new[] { PlayerId }).AsCollection);
            Assert.That(pushes.Single(p => p.Escalated).UserIds, Does.Contain(GameMasterId));
            Assert.That(pushes.Single(p => p.Escalated).UserIds, Does.Not.Contain(PlayerId));
        });
    }

    [Test]
    public async Task Nudge_HoldsThePushUntilTheGuildIsOutOfItsQuietHours()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedSceneAsync(deadline: now.AddHours(-1));

        // A window covering the whole day, so the pass is inside it whenever this test runs.
        _context.GuildQuietHoursConfigs.Add(new GuildQuietHoursConfig
        {
            GuildId = GuildId, Enabled = true, StartMinuteLocal = 0, EndMinuteLocal = 1439,
            TimeZoneId = "UTC", UpdatedAt = now,
        });

        await _context.SaveChangesAsync();

        var outcome = await BuildNudges().SendDueNudgesAsync();
        var state = await _context.Set<SceneState>().AsNoTracking().FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Deferred, Is.EqualTo(1));
            Assert.That(outcome.Nudged, Is.Zero);
            Assert.That(_bus.Published.OfType<SceneTurnPushRequested>(), Is.Empty);
            Assert.That(state.NudgeCount, Is.Zero, "nothing was recorded, so the next pass chases it");
        });
    }

    [Test]
    public async Task Nudge_SendsNoPushToSomebodyWhoTurnedMobilePushOff()
    {
        await SeedAsync();
        await SeedSceneAsync(deadline: DateTimeOffset.UtcNow.AddHours(-1));

        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = "gnst-player", MemberId = "memb-player", Level = NotificationLevel.AllMessages,
            MobilePush = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();

        var outcome = await BuildNudges().SendDueNudgesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Nudged, Is.EqualTo(1), "the hub event still goes out");
            Assert.That(_bus.Published.OfType<SceneTurnPushRequested>(), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Helpers
    // ══════════════════════════════════════════════════════════════════════

    private async Task<SceneListDto?> ListAsync(
        string userId, bool waitingOnMe = false, bool includeConcluded = false, bool includeArchived = false)
    {
        var result = await _endpoint.ListAsync(
            GuildId, _permissions, _personas, _cast, _context, TestPrincipal.Create(userId),
            waitingOnMe, includeConcluded, includeArchived);

        return (result as Ok<SceneListDto>)?.Value;
    }

    /// <summary>The code a refusal carries, or null when the result is not one.</summary>
    private static string? FaultCode(IResult result)
    {
        var value = (result as IValueHttpResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    private Task<IResult> CreateAsync(CreateSceneDto dto, string userId = GameMasterId) =>
        _endpoint.CreateAsync(GuildId, ChannelId, dto, _permissions, _scenes, _auditLog, _hydrate,
            _hub, _bus, _context, TestPrincipal.Create(userId));

    private SceneNudgeService BuildNudges() =>
        new(_context, _scenes, _cast, new ModulePermissionHolderService(_context, _permissions),
            new NotificationResolutionService(_context), _hub,
            _bus, NullLogger<SceneNudgeService>.Instance);

    private List<string> Nudged() =>
        ((FakeHubClients)_hub.Clients).RecipientsOf(SceneService.NudgeEvent);
}
