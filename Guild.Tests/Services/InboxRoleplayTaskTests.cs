using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// The roleplay half of the Waiting-on-you tab: a turn on the clock, an approval queue, and a
/// character a reviewer sent back.
/// </summary>
[TestFixture]
public class InboxRoleplayTaskTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string PlayerId = "user-player";
    private const string ReviewerId = "user-reviewer";
    private const string OutsiderId = "user-outsider";

    private const string ParentChannelId = "chan-ic";
    private const string SceneChannelId = "scene-1";

    private const string EveryoneRoleId = "role-everyone";
    private const string ReviewerRoleId = "role-reviewer";

    private const string PlayerPersonaId = "pers_mayor";
    private const string GuildPersonaId = "pers_narrator";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private InboxTaskService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());

        var permissions = new GuildPermissionService(
            _cache, _context, NullLogger<GuildPermissionService>.Instance);

        _service = new InboxTaskService(_context, permissions);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════ Seeding
    // ══════════════════════════════════════════════════════════════════════

    private async Task SeedAsync(
        GuildFeatures features = GuildFeatures.Personas | GuildFeatures.Scenes | GuildFeatures.Wiki)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Blackwater", Features = features,
            Kind = GuildKind.Roleplay, CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            ModulePermissions = ModulePermissions.UsePersonas,
            CreatedAt = now, UpdatedAt = now,
        });

        // Only the reviewer holds ApprovePersonas, so the queue is a real subset of the fixture.
        _context.Roles.Add(new Role
        {
            Id = ReviewerRoleId, GuildId = GuildId, Name = "Storyteller",
            ModulePermissions = ModulePermissions.ApprovePersonas | ModulePermissions.ManageAnyPersona,
            CreatedAt = now, UpdatedAt = now,
        });

        AddMember("memb-player", PlayerId);
        AddMember("memb-reviewer", ReviewerId);
        AddMember("memb-outsider", OutsiderId);

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-reviewer-role", RoleId = ReviewerRoleId, MemberId = "memb-reviewer",
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Channels.Add(new Channel
        {
            Id = ParentChannelId, GuildId = GuildId, Name = "the-inn", Type = ChannelType.Text,
            CreatedAt = now, UpdatedAt = now,
        });

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
                Id = $"rm-{memberId}", RoleId = EveryoneRoleId, MemberId = memberId,
                CreatedAt = now, UpdatedAt = now,
            });
        }
    }

    private async Task AddPersonaAsync(
        string personaId, string name, string? ownerUserId,
        PersonaApprovalState approval = PersonaApprovalState.Approved,
        string? displayName = null, string? reason = null, bool retired = false)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Set<Persona>().Add(new Persona
        {
            Id = personaId,
            Scope = ownerUserId is null ? PersonaScope.Guild : PersonaScope.User,
            OwnerUserId = ownerUserId,
            OwnerGuildId = ownerUserId is null ? GuildId : null,
            Name = name, IsRetired = retired,
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"profile-{personaId}", PersonaId = personaId, GuildId = GuildId,
            DisplayName = displayName, ApprovalState = approval, ChangesRequestedReason = reason,
            CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
    }

    private async Task AddGrantAsync(string personaId, string? userId = null, string? roleId = null)
    {
        _context.Set<PersonaGrant>().Add(PersonaGrant.Create(new CreatePersonaGrantParams
        {
            PersonaId = personaId, UserId = userId, RoleId = roleId,
        }));

        await _context.SaveChangesAsync();
    }

    private async Task<SceneState> AddSceneAsync(
        string? currentTurn, DateTimeOffset? deadline = null, SceneStatus status = SceneStatus.Active)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Channels.Add(new Channel
        {
            Id = SceneChannelId, GuildId = GuildId, Name = "The Siege of Blackwater",
            Type = ChannelType.Scene, ParentChannelId = ParentChannelId,
            CreatedAt = now, UpdatedAt = now,
        });

        var state = SceneState.Create(new CreateSceneStateParams
        {
            ChannelId = SceneChannelId, GuildId = GuildId, TurnLengthHours = 48,
            ParticipantPersonaIds = [PlayerPersonaId],
        });

        state.Status = status;
        state.CurrentTurnPersonaId = currentTurn;
        state.TurnStartedAt = now;
        state.TurnDeadlineAt = deadline;

        _context.Set<SceneState>().Add(state);
        await _context.SaveChangesAsync();

        return state;
    }

    private async Task<List<InboxTaskDto>> TasksAsync(string userId) =>
        (await _service.GetTasksAsync(userId, InboxTaskService.DefaultPageSize)).Tasks.ToList();

    // ══════════════════════════════════════════════════════════════════════ Scene turns
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SceneTurn_OnYourOwnCharacter_IsWaitingOnYou()
    {
        await SeedAsync();
        await AddPersonaAsync(PlayerPersonaId, "Mayor Cogsgrove", PlayerId);
        var deadline = DateTimeOffset.UtcNow.AddHours(6);
        await AddSceneAsync(PlayerPersonaId, deadline);

        var task = (await TasksAsync(PlayerId)).SingleOrDefault(t => t.Kind == InboxTaskKind.SceneTurn);

        Assert.That(task, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(task!.TargetId, Is.EqualTo(SceneChannelId));
            Assert.That(task.Title, Is.EqualTo("The Siege of Blackwater"));
            Assert.That(task.Subtitle, Does.Contain("Mayor Cogsgrove"));
            Assert.That(task.Breadcrumb.ChannelId, Is.EqualTo(SceneChannelId));
            Assert.That(task.DueAt, Is.EqualTo(deadline).Within(TimeSpan.FromSeconds(1)));
            Assert.That(task.IsOverdue, Is.False);
        });
    }

    [Test]
    public async Task SceneTurn_OnSomebodyElsesCharacter_IsNotWaitingOnYou()
    {
        await SeedAsync();
        await AddPersonaAsync(PlayerPersonaId, "Mayor Cogsgrove", PlayerId);
        await AddSceneAsync(PlayerPersonaId, DateTimeOffset.UtcNow.AddHours(6));

        Assert.That(await TasksAsync(OutsiderId), Is.Empty,
            "the turn belongs to a character the outsider does not answer for");
    }

    [Test]
    public async Task SceneTurn_OnAGuildCharacterYouHoldAGrantOn_IsWaitingOnYou()
    {
        await SeedAsync();
        await AddPersonaAsync(GuildPersonaId, "Narrator", ownerUserId: null);
        await AddGrantAsync(GuildPersonaId, userId: ReviewerId);
        await AddSceneAsync(GuildPersonaId, DateTimeOffset.UtcNow.AddHours(6));

        var kinds = (await TasksAsync(ReviewerId)).Select(t => t.Kind);

        Assert.That(kinds, Does.Contain(InboxTaskKind.SceneTurn),
            "a shared character has no owner, so the grant is what says who answers for it");
    }

    [Test]
    public async Task SceneTurn_OnAPausedScene_IsNotWaitingOnAnybody()
    {
        await SeedAsync();
        await AddPersonaAsync(PlayerPersonaId, "Mayor Cogsgrove", PlayerId);
        await AddSceneAsync(PlayerPersonaId, DateTimeOffset.UtcNow.AddHours(6), SceneStatus.Paused);

        Assert.That(await TasksAsync(PlayerId), Is.Empty,
            "the turn only moves, and only counts, while a scene is active");
    }

    [Test]
    public async Task SceneTurn_WithTheScenesModuleOff_Disappears()
    {
        await SeedAsync(features: GuildFeatures.Personas);
        await AddPersonaAsync(PlayerPersonaId, "Mayor Cogsgrove", PlayerId);
        await AddSceneAsync(PlayerPersonaId, DateTimeOffset.UtcNow.AddHours(6));

        Assert.That(await TasksAsync(PlayerId), Is.Empty);
    }

    [Test]
    public async Task SceneTurn_PastItsDeadline_IsOverdue()
    {
        await SeedAsync();
        await AddPersonaAsync(PlayerPersonaId, "Mayor Cogsgrove", PlayerId);
        await AddSceneAsync(PlayerPersonaId, DateTimeOffset.UtcNow.AddHours(-2));

        var task = (await TasksAsync(PlayerId)).Single();

        Assert.That(task.IsOverdue, Is.True, "a turn has no grace period of its own");
    }

    [Test]
    public async Task SceneTurn_OnARetiredCharacter_IsNotWaitingOnAnybody()
    {
        await SeedAsync();
        await AddPersonaAsync(PlayerPersonaId, "Mayor Cogsgrove", PlayerId, retired: true);
        await AddSceneAsync(PlayerPersonaId, DateTimeOffset.UtcNow.AddHours(6));

        Assert.That(await TasksAsync(PlayerId), Is.Empty,
            "retiring is how somebody puts a character away, and a chased turn is what they put away");
    }

    // ══════════════════════════════════════════════════════════════════════ The approval queue
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PersonaReview_ReachesTheReviewerAndNobodyElse()
    {
        await SeedAsync();
        await AddPersonaAsync(
            PlayerPersonaId, "Mayor Cogsgrove", PlayerId, PersonaApprovalState.Submitted);

        var reviewer = (await TasksAsync(ReviewerId))
            .SingleOrDefault(t => t.Kind == InboxTaskKind.PersonaReview);
        var outsider = await TasksAsync(OutsiderId);

        Assert.That(reviewer, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(reviewer!.TargetId, Is.EqualTo(PlayerPersonaId));
            Assert.That(reviewer.Title, Is.EqualTo("Mayor Cogsgrove"));
            Assert.That(reviewer.Breadcrumb.ChannelId, Is.Null,
                "a queue lives in the guild's cast rather than in any one channel");
            Assert.That(reviewer.Breadcrumb.GuildName, Is.EqualTo("Blackwater"));
            Assert.That(outsider.Any(t => t.Kind == InboxTaskKind.PersonaReview), Is.False,
                "a member without ApprovePersonas cannot see the queue on the route either");
        });
    }

    [Test]
    public async Task PersonaReview_UsesThePerGuildDisplayName()
    {
        await SeedAsync();
        await AddPersonaAsync(
            PlayerPersonaId, "Mayor Cogsgrove", PlayerId, PersonaApprovalState.Submitted,
            displayName: "The Mayor");

        var task = (await TasksAsync(ReviewerId)).Single(t => t.Kind == InboxTaskKind.PersonaReview);

        Assert.That(task.Title, Is.EqualTo("The Mayor"));
    }

    [Test]
    public async Task PersonaReview_OfAnAlreadyApprovedCharacter_IsNotAQueueRow()
    {
        await SeedAsync();
        await AddPersonaAsync(PlayerPersonaId, "Mayor Cogsgrove", PlayerId);

        Assert.That(await TasksAsync(ReviewerId), Is.Empty);
    }

    [Test]
    public async Task PersonaChangesRequested_ReachesTheCharactersOwnerWithTheReason()
    {
        await SeedAsync();
        await AddPersonaAsync(
            PlayerPersonaId, "Mayor Cogsgrove", PlayerId, PersonaApprovalState.ChangesRequested,
            reason: "Give him a reason to be in town.");

        var task = (await TasksAsync(PlayerId)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(task.Kind, Is.EqualTo(InboxTaskKind.PersonaChangesRequested));
            Assert.That(task.Subtitle, Is.EqualTo("Give him a reason to be in town."));
            Assert.That(task.DueAt, Is.Null);
        });
    }

    [Test]
    public async Task PersonaChangesRequested_OnSomebodyElsesCharacter_IsNotWaitingOnYou()
    {
        await SeedAsync();
        await AddPersonaAsync(
            PlayerPersonaId, "Mayor Cogsgrove", PlayerId, PersonaApprovalState.ChangesRequested,
            reason: "Give him a reason to be in town.");

        Assert.That(
            (await TasksAsync(OutsiderId)).Any(t => t.Kind == InboxTaskKind.PersonaChangesRequested),
            Is.False);
    }

    [Test]
    public async Task PersonaChangesRequested_OnAGuildCharacter_ReachesWhoeverManagesCharacters()
    {
        await SeedAsync();
        await AddPersonaAsync(
            GuildPersonaId, "Narrator", ownerUserId: null,
            approval: PersonaApprovalState.ChangesRequested, reason: "Too many hats.");

        var reviewer = await TasksAsync(ReviewerId);
        var player = await TasksAsync(PlayerId);

        Assert.Multiple(() =>
        {
            Assert.That(
                reviewer.Any(t => t.Kind == InboxTaskKind.PersonaChangesRequested), Is.True,
                "nobody owns a guild character, so it lands on ManageAnyPersona");
            Assert.That(
                player.Any(t => t.Kind == InboxTaskKind.PersonaChangesRequested), Is.False);
        });
    }

    [Test]
    public async Task PersonaTasks_WithThePersonasModuleOff_Disappear()
    {
        await SeedAsync(features: GuildFeatures.Wiki);
        await AddPersonaAsync(
            PlayerPersonaId, "Mayor Cogsgrove", PlayerId, PersonaApprovalState.Submitted);

        Assert.That(await TasksAsync(ReviewerId), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════ The badge
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Count_IncludesTheRoleplayRows()
    {
        await SeedAsync();
        await AddPersonaAsync(PlayerPersonaId, "Mayor Cogsgrove", PlayerId);
        await AddSceneAsync(PlayerPersonaId, DateTimeOffset.UtcNow.AddHours(6));

        Assert.That(await _service.CountAsync(PlayerId), Is.EqualTo(1),
            "the header badge counts the same rows the tab lists");
    }
}
