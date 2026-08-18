using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
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

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeInvokingMessageBus _bus = null!;
    private GuildPermissionService _permissions = null!;
    private GuildHydrateService _hydrate = null!;
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
        _scenes = new SceneService(
            _context, new PersonaMentionService(_context, new PersonaService(_cache, _context)), _hydrate, _hub);
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
        string? currentTurn = PlayerPersonaId, DateTimeOffset? deadline = null)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Channels.Add(new Channel
        {
            Id = "scene-1", GuildId = GuildId, Name = "The Siege of Blackwater", Type = ChannelType.Scene,
            ParentChannelId = ChannelId, CreatedByUserId = GameMasterId, CreatedAt = now, UpdatedAt = now,
        });

        var state = SceneState.Create(new CreateSceneStateParams
        {
            ChannelId = "scene-1", GuildId = GuildId, TurnLengthHours = 48,
            ParticipantPersonaIds = [PlayerPersonaId, OtherPersonaId, GmPersonaId],
        });

        state.Status = SceneStatus.Active;
        state.CurrentTurnPersonaId = currentTurn;
        state.TurnDeadlineAt = deadline;

        _context.Set<SceneState>().Add(state);
        await _context.SaveChangesAsync();

        return state;
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

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
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

    // ══════════════════════════════════════════════════════════════════════ Helpers
    // ══════════════════════════════════════════════════════════════════════

    private Task<IResult> CreateAsync(CreateSceneDto dto, string userId = GameMasterId) =>
        _endpoint.CreateAsync(GuildId, ChannelId, dto, _permissions, _scenes, _auditLog, _hydrate,
            _hub, _bus, _context, TestPrincipal.Create(userId));

    private SceneNudgeService BuildNudges() =>
        new(_context, _scenes, _permissions, _hub, NullLogger<SceneNudgeService>.Instance);

    private List<string> Nudged() =>
        ((FakeHubClients)_hub.Clients).RecipientsOf(SceneService.NudgeEvent);
}
