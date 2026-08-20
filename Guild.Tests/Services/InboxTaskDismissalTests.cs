using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// The X on a Waiting-on-you row. The tab is derived state, so a dismissal is the only thing that
/// can put a row away - and it has to let the row back in when the thing it was about moves on.
/// </summary>
[TestFixture]
public class InboxTaskDismissalTests
{
    private const string GuildId = "guild-1";
    private const string OtherGuildId = "guild-2";
    private const string OwnerId = "owner-1";
    private const string PlayerId = "user-player";
    private const string StrangerId = "user-stranger";

    private const string ParentChannelId = "chan-ic";
    private const string SceneChannelId = "scene-1";

    private const string PersonaId = "pers_mayor";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private InboxTaskService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());

        var permissions = PermissionTestFactory.Create(_cache, _context);

        _service = new InboxTaskService(_context, permissions);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════ Scene turns
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Dismiss_ATurnThatIsWaitingOnYou_TakesItOffTheTabAndTheBadge()
    {
        await SeedGuildAsync(GuildId);
        await AddPersonaAsync(GuildId, PersonaApprovalState.Approved);
        await AddSceneAsync();

        Assert.That(await KindsAsync(PlayerId), Does.Contain(InboxTaskKind.SceneTurn));

        var dismissed = await _service.DismissAsync(
            PlayerId, InboxTaskKind.SceneTurn, GuildId, SceneChannelId);

        var remaining = await KindsAsync(PlayerId);
        var badge = await _service.CountAsync(PlayerId);

        Assert.Multiple(() =>
        {
            Assert.That(dismissed, Is.True);
            Assert.That(remaining, Is.Empty);
            Assert.That(badge, Is.Zero, "the badge counts the same rows the tab lists");
        });
    }

    [Test]
    public async Task Dismiss_ThenTheTurnComesRoundAgain_PutsTheRowBack()
    {
        await SeedGuildAsync(GuildId);
        await AddPersonaAsync(GuildId, PersonaApprovalState.Approved);
        var scene = await AddSceneAsync();

        await _service.DismissAsync(PlayerId, InboxTaskKind.SceneTurn, GuildId, SceneChannelId);
        Assert.That(await KindsAsync(PlayerId), Is.Empty);

        // What Advance does to a scene handed back to the same character.
        scene.TurnStartedAt = DateTimeOffset.UtcNow.AddMinutes(5);
        scene.TurnNumber++;
        await _context.SaveChangesAsync();

        Assert.That(await KindsAsync(PlayerId), Does.Contain(InboxTaskKind.SceneTurn),
            "a later turn of the same scene is new work, not work already put away");
    }

    [Test]
    public async Task Dismiss_Twice_RestampsTheOneRowRatherThanWritingASecond()
    {
        await SeedGuildAsync(GuildId);
        await AddPersonaAsync(GuildId, PersonaApprovalState.Approved);
        await AddSceneAsync();

        await _service.DismissAsync(PlayerId, InboxTaskKind.SceneTurn, GuildId, SceneChannelId);

        // Back-dated rather than pushing the turn into the future, which is a state no scene is
        // ever in: this is the row coming back and being put away a second time.
        var first = await _context.InboxTaskDismissals.SingleAsync();
        first.DismissedAt = first.DismissedAt.AddMinutes(-10);
        await _context.SaveChangesAsync();

        Assert.That(await KindsAsync(PlayerId), Does.Contain(InboxTaskKind.SceneTurn));

        await _service.DismissAsync(PlayerId, InboxTaskKind.SceneTurn, GuildId, SceneChannelId);

        var stored = await _context.InboxTaskDismissals.CountAsync();
        var remaining = await KindsAsync(PlayerId);

        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.EqualTo(1));
            Assert.That(remaining, Is.Empty);
        });
    }

    /// <summary>The sweep runs on the same write, so the row being re-stamped has to survive it -
    /// the database still holds the old timestamp when the sweep asks.</summary>
    [Test]
    public async Task Dismiss_ARowLastPutAwayBeyondRetention_StillPutsItAway()
    {
        await SeedGuildAsync(GuildId);
        await AddPersonaAsync(GuildId, PersonaApprovalState.Approved);
        await AddSceneAsync();

        await _service.DismissAsync(PlayerId, InboxTaskKind.SceneTurn, GuildId, SceneChannelId);

        var first = await _context.InboxTaskDismissals.SingleAsync();
        first.DismissedAt = DateTimeOffset.UtcNow.AddDays(-120);
        await _context.SaveChangesAsync();

        await _service.DismissAsync(PlayerId, InboxTaskKind.SceneTurn, GuildId, SceneChannelId);

        var stored = await _context.InboxTaskDismissals.CountAsync();
        var remaining = await KindsAsync(PlayerId);

        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.EqualTo(1));
            Assert.That(remaining, Is.Empty);
        });
    }

    [Test]
    public async Task Dismiss_SweepsTheCallersOtherDismissalsPastRetention()
    {
        await SeedGuildAsync(GuildId);
        await AddPersonaAsync(GuildId, PersonaApprovalState.Approved);
        await AddSceneAsync();

        await _service.DismissAsync(PlayerId, InboxTaskKind.ChoreDue, GuildId, "choc-gone");

        var old = await _context.InboxTaskDismissals.SingleAsync();
        old.DismissedAt = DateTimeOffset.UtcNow.AddDays(-120);
        await _context.SaveChangesAsync();

        await _service.DismissAsync(PlayerId, InboxTaskKind.SceneTurn, GuildId, SceneChannelId);

        var kinds = await _context.InboxTaskDismissals.Select(d => d.Kind).ToListAsync();

        Assert.That(kinds, Is.EqualTo(new[] { nameof(InboxTaskKind.SceneTurn) }),
            "a dismissal outlives the row it was about by 90 days and no longer");
    }

    // ══════════════════════════════════════════════════════════════════════ Approval queue
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Dismiss_AReviewRow_ThenAResubmission_PutsTheRowBack()
    {
        await SeedGuildAsync(GuildId);
        var profile = await AddPersonaAsync(GuildId, PersonaApprovalState.Submitted);

        Assert.That(await KindsAsync(PlayerId), Does.Contain(InboxTaskKind.PersonaReview));

        await _service.DismissAsync(PlayerId, InboxTaskKind.PersonaReview, GuildId, PersonaId);
        Assert.That(await KindsAsync(PlayerId), Is.Empty);

        // Every transition on a profile moves UpdatedAt, which is the stamp a review row carries.
        profile.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await _context.SaveChangesAsync();

        Assert.That(await KindsAsync(PlayerId), Does.Contain(InboxTaskKind.PersonaReview));
    }

    [Test]
    public async Task Dismiss_InOneGuild_LeavesTheSameCharactersQueueInAnother()
    {
        await SeedGuildAsync(GuildId);
        await SeedGuildAsync(OtherGuildId);
        await AddPersonaAsync(GuildId, PersonaApprovalState.Submitted);
        await AddPersonaAsync(OtherGuildId, PersonaApprovalState.Submitted, addPersona: false);

        await _service.DismissAsync(PlayerId, InboxTaskKind.PersonaReview, GuildId, PersonaId);

        var rows = (await _service.GetTasksAsync(PlayerId, InboxTaskService.DefaultPageSize)).Tasks
            .Where(t => t.Kind == InboxTaskKind.PersonaReview)
            .ToList();

        Assert.That(rows, Has.Count.EqualTo(1),
            "a character can be submitted in two guilds at once and the target id is the same in both");
        Assert.That(rows[0].Breadcrumb.GuildId, Is.EqualTo(OtherGuildId));
    }

    // ══════════════════════════════════════════════════════════════════════ Refusals
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Dismiss_ByAStrangerToTheGuild_IsRefusedAndWritesNothing()
    {
        await SeedGuildAsync(GuildId);
        await AddPersonaAsync(GuildId, PersonaApprovalState.Approved);
        await AddSceneAsync();

        var dismissed = await _service.DismissAsync(
            StrangerId, InboxTaskKind.SceneTurn, GuildId, SceneChannelId);

        var wrote = await _context.InboxTaskDismissals.AnyAsync();

        Assert.Multiple(() =>
        {
            Assert.That(dismissed, Is.False);
            Assert.That(wrote, Is.False);
        });
    }

    [Test]
    public async Task Dismiss_OfOneKind_LeavesAnotherKindOnTheSameTarget()
    {
        await SeedGuildAsync(GuildId);
        await AddPersonaAsync(GuildId, PersonaApprovalState.Submitted);
        await AddSceneAsync();

        await _service.DismissAsync(PlayerId, InboxTaskKind.SceneTurn, GuildId, SceneChannelId);

        Assert.That(await KindsAsync(PlayerId), Is.EqualTo(new[] { InboxTaskKind.PersonaReview }));
    }

    [Test]
    public async Task Dismiss_IsPerPerson()
    {
        await SeedGuildAsync(GuildId);
        await AddPersonaAsync(GuildId, PersonaApprovalState.Submitted);

        await _service.DismissAsync(PlayerId, InboxTaskKind.PersonaReview, GuildId, PersonaId);

        var mine = await KindsAsync(PlayerId);
        var theirs = await KindsAsync(OwnerId);

        Assert.Multiple(() =>
        {
            Assert.That(mine, Is.Empty);
            Assert.That(theirs, Does.Contain(InboxTaskKind.PersonaReview),
                "the other reviewer never put it away");
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Seeding
    // ══════════════════════════════════════════════════════════════════════

    private async Task<List<InboxTaskKind>> KindsAsync(string userId) =>
        (await _service.GetTasksAsync(userId, InboxTaskService.DefaultPageSize)).Tasks
        .Select(t => t.Kind)
        .ToList();

    private async Task SeedGuildAsync(string guildId)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = guildId, OwnerId = OwnerId, Name = $"Blackwater {guildId}",
            Features = GuildFeatures.Personas | GuildFeatures.Scenes,
            Kind = GuildKind.Roleplay, CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.Add(new Role
        {
            Id = $"role-everyone-{guildId}", GuildId = guildId, Name = "everyone",
            Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            ModulePermissions = ModulePermissions.UsePersonas
                                | ModulePermissions.ApprovePersonas
                                | ModulePermissions.ManageAnyPersona,
            CreatedAt = now, UpdatedAt = now,
        });

        AddMember($"memb-player-{guildId}", PlayerId);
        AddMember($"memb-owner-{guildId}", OwnerId);

        _context.Channels.Add(new Channel
        {
            Id = $"{ParentChannelId}-{guildId}", GuildId = guildId, Name = "the-inn",
            Type = ChannelType.Text, CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
        return;

        void AddMember(string memberId, string userId)
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = memberId, GuildId = guildId, UserId = userId, JoinedAt = DateTime.UtcNow,
                CreatedAt = now, UpdatedAt = now, SearchValue = $"{userId}#{guildId}",
            });

            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-{memberId}", RoleId = $"role-everyone-{guildId}", MemberId = memberId,
                CreatedAt = now, UpdatedAt = now,
            });
        }
    }

    private async Task<PersonaGuildProfile> AddPersonaAsync(
        string guildId, PersonaApprovalState approval, bool addPersona = true)
    {
        var now = DateTimeOffset.UtcNow;

        if (addPersona)
        {
            _context.Set<Persona>().Add(new Persona
            {
                Id = PersonaId, Scope = PersonaScope.User, OwnerUserId = PlayerId,
                Name = "Mayor Cogsgrove", CreatedAt = now, UpdatedAt = now,
            });
        }

        var profile = new PersonaGuildProfile
        {
            Id = $"profile-{guildId}", PersonaId = PersonaId, GuildId = guildId,
            ApprovalState = approval, CreatedAt = now, UpdatedAt = now,
        };

        _context.Set<PersonaGuildProfile>().Add(profile);
        await _context.SaveChangesAsync();

        return profile;
    }

    private async Task<SceneState> AddSceneAsync()
    {
        var now = DateTimeOffset.UtcNow;

        _context.Channels.Add(new Channel
        {
            Id = SceneChannelId, GuildId = GuildId, Name = "The Siege of Blackwater",
            Type = ChannelType.Scene, ParentChannelId = $"{ParentChannelId}-{GuildId}",
            CreatedAt = now, UpdatedAt = now,
        });

        var state = SceneState.Create(new CreateSceneStateParams
        {
            ChannelId = SceneChannelId, GuildId = GuildId, TurnLengthHours = 48,
            ParticipantPersonaIds = [PersonaId],
        });

        state.Status = SceneStatus.Active;
        state.CurrentTurnPersonaId = PersonaId;
        state.TurnStartedAt = now;
        state.TurnDeadlineAt = now.AddHours(48);

        _context.Set<SceneState>().Add(state);
        await _context.SaveChangesAsync();

        return state;
    }
}
