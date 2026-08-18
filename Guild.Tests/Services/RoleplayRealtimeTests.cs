using Guild.Application.Dtos;
using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// The roleplay hub events, concentrating on who each one reaches. A cast change is guild-wide; a
/// review, a grant and an autoproxy state are not, and putting either of the last two on a
/// guild-wide broadcast would say who plays whom.
/// </summary>
[TestFixture]
public class RoleplayRealtimeTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "chan-1";

    private const string OwnerId = "user-owner";
    private const string PlayerId = "user-player";
    private const string ReviewerId = "user-reviewer";
    private const string BystanderId = "user-bystander";

    private const string EveryoneRoleId = "role-everyone";
    private const string ReviewerRoleId = "role-reviewer";

    private const string PersonaId = "pers_mayor";
    private const string GuildPersonaId = "pers_narrator";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private GuildPermissionService _permissions = null!;
    private PersonaService _personas = null!;
    private PersonaPageService _pages = null!;
    private PersonaDisplayGuard _displayGuard = null!;
    private AuditLogService _auditLog = null!;
    private RoleplayRealtimeService _realtime = null!;
    private PersonaEndpoint _endpoint = null!;
    private PersonaProfileEndpoint _profiles = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _personas = new PersonaService(_cache, _context);
        _pages = new PersonaPageService(_context);
        _displayGuard = new PersonaDisplayGuard(_context);
        _auditLog = new AuditLogService(_context);
        _endpoint = new PersonaEndpoint();
        _profiles = new PersonaProfileEndpoint();

        // Everybody in the fixture is online, so a guild-wide broadcast that reaches fewer people
        // than it should is a fault in the event rather than in the presence set.
        _realtime = RoleplayTestFactory.CreateRealtime(
            _context, _permissions, _personas, _hub,
            RedisTestFactory.CreateWithPresence(
                Present("memb-owner", OwnerId),
                Present("memb-player", PlayerId),
                Present("memb-reviewer", ReviewerId),
                Present("memb-bystander", BystanderId)));
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static MemberPresenceState Present(string memberId, string userId) => new()
    {
        MemberId = memberId, UserId = userId, Status = "Online",
    };

    private async Task SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Blackwater",
            Features = GuildFeatures.Personas | GuildFeatures.Scenes | GuildFeatures.Wiki,
            Kind = GuildKind.Roleplay, CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            ModulePermissions = ModulePermissions.UsePersonas,
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.Add(new Role
        {
            Id = ReviewerRoleId, GuildId = GuildId, Name = "Storyteller",
            ModulePermissions = ModulePermissions.ApprovePersonas | ModulePermissions.ManageAnyPersona,
            CreatedAt = now, UpdatedAt = now,
        });

        AddMember("memb-player", PlayerId);
        AddMember("memb-reviewer", ReviewerId);
        AddMember("memb-bystander", BystanderId);

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-reviewer-role", RoleId = ReviewerRoleId, MemberId = "memb-reviewer",
            CreatedAt = now, UpdatedAt = now,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "the-inn", Type = ChannelType.Text,
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
        PersonaApprovalState approval = PersonaApprovalState.Draft)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Set<Persona>().Add(new Persona
        {
            Id = personaId,
            Scope = ownerUserId is null ? PersonaScope.Guild : PersonaScope.User,
            OwnerUserId = ownerUserId,
            OwnerGuildId = ownerUserId is null ? GuildId : null,
            Name = name, CreatedAt = now, UpdatedAt = now,
        });

        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"profile-{personaId}", PersonaId = personaId, GuildId = GuildId,
            ApprovalState = approval, CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
    }

    private FakeHubClients Clients => (FakeHubClients)_hub.Clients;

    private List<string> RecipientsOf(string method) => Clients.RecipientsOf(method);

    private object? PayloadOf(string method) =>
        Clients.SentMessages.LastOrDefault(m => m.Method == method).Args?.FirstOrDefault();

    private static T? Field<T>(object? payload, string name) =>
        (T?)payload?.GetType().GetProperty(name)?.GetValue(payload);

    // ══════════════════════════════════════════════════════════════════════ Characters
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PersonaCreated_ForAPersonalCharacter_ReachesItsOwnerAlone()
    {
        await SeedAsync();

        await _endpoint.CreateOwnAsync(
            new CreatePersonaDto { Name = "Mayor Cogsgrove" },
            _realtime, _context, TestPrincipal.Create(PlayerId));

        Assert.That(
            RecipientsOf(RoleplayRealtimeService.PersonaCreatedEvent),
            Is.EqualTo(new[] { PlayerId }),
            "a character adopted nowhere is nobody else's business yet");
    }

    [Test]
    public async Task PersonaCreated_ForAGuildCharacter_ReachesTheGuild()
    {
        await SeedAsync();

        await _endpoint.CreateForGuildAsync(GuildId,
            new CreatePersonaDto { Name = "Narrator" },
            _permissions, _personas, _displayGuard, _auditLog, _pages, _realtime, _context,
            TestPrincipal.Create(ReviewerId));

        Assert.Multiple(() =>
        {
            Assert.That(
                RecipientsOf(RoleplayRealtimeService.PersonaCreatedEvent),
                Is.EquivalentTo(new[] { OwnerId, PlayerId, ReviewerId, BystanderId }));
            Assert.That(
                RecipientsOf(RoleplayRealtimeService.PersonaAdoptedEvent), Is.Not.Empty,
                "a guild character is adopted into its own guild at creation");
        });
    }

    [Test]
    public async Task PersonaUpdated_CarriesEnoughToPatchACacheRatherThanDropIt()
    {
        await SeedAsync();
        await AddPersonaAsync(PersonaId, "Mayor Cogsgrove", PlayerId);

        await _endpoint.UpdateOwnAsync(PersonaId,
            new UpdatePersonaDto { Color = Optional<string>.Of("#4F8A6B"), Pronouns = Optional<string>.Of("he/him") },
            _personas, _displayGuard, _realtime, _context, TestPrincipal.Create(PlayerId));

        var payload = PayloadOf(RoleplayRealtimeService.PersonaUpdatedEvent);

        Assert.Multiple(() =>
        {
            Assert.That(Field<string>(payload, "Color"), Is.EqualTo("#4F8A6B"));
            Assert.That(Field<string>(payload, "Pronouns"), Is.EqualTo("he/him"));
            Assert.That(Field<string>(payload, "Name"), Is.EqualTo("Mayor Cogsgrove"));
        });
    }

    [Test]
    public async Task PersonaDeleted_SaysWhetherItWasRetired()
    {
        await SeedAsync();
        await AddPersonaAsync(PersonaId, "Mayor Cogsgrove", PlayerId);

        var persona = await _context.Set<Persona>().FindAsync(PersonaId);
        persona!.HasSpoken = true;
        await _context.SaveChangesAsync();

        await _endpoint.DeleteOwnAsync(
            PersonaId, _personas, _realtime, _context, TestPrincipal.Create(PlayerId));

        var payload = PayloadOf(RoleplayRealtimeService.PersonaDeletedEvent);

        Assert.Multiple(() =>
        {
            Assert.That(RecipientsOf(RoleplayRealtimeService.PersonaDeletedEvent),
                Does.Contain(BystanderId),
                "the guild was rendering it, so the guild has to stop");
            Assert.That(Field<bool?>(payload, "Retired"), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ The approval queue
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ReviewRequested_ReachesTheReviewersAndTheCharactersPlayer()
    {
        await SeedAsync();
        await AddPersonaAsync(PersonaId, "Mayor Cogsgrove", PlayerId);

        await _profiles.SubmitAsync(GuildId, PersonaId,
            _permissions, _personas, _pages, _realtime, _context, TestPrincipal.Create(PlayerId));

        Assert.Multiple(() =>
        {
            Assert.That(
                RecipientsOf(RoleplayRealtimeService.ReviewRequestedEvent),
                Is.EquivalentTo(new[] { OwnerId, ReviewerId, PlayerId }));
            Assert.That(
                RecipientsOf(RoleplayRealtimeService.PersonaProfileChangedEvent),
                Does.Contain(BystanderId),
                "the state change is still guild-wide - only the queue itself is not");
        });
    }

    [Test]
    public async Task ReviewCompleted_KeepsTheReasonOffTheGuildWideEvent()
    {
        await SeedAsync();
        await AddPersonaAsync(PersonaId, "Mayor Cogsgrove", PlayerId, PersonaApprovalState.Submitted);

        await _profiles.RequestChangesAsync(GuildId, PersonaId,
            new RequestPersonaChangesDto { Reason = "Give him a reason to be in town." },
            _permissions, _personas, _pages, _auditLog, _realtime, _context,
            TestPrincipal.Create(ReviewerId));

        var completed = PayloadOf(RoleplayRealtimeService.ReviewCompletedEvent);
        var guildWide = PayloadOf(RoleplayRealtimeService.PersonaProfileChangedEvent);

        Assert.Multiple(() =>
        {
            Assert.That(Field<string>(completed, "Reason"), Is.EqualTo("Give him a reason to be in town."));
            Assert.That(Field<string>(completed, "ReviewedByUserId"), Is.EqualTo(ReviewerId));
            Assert.That(Field<bool?>(completed, "Approved"), Is.False);
            Assert.That(
                RecipientsOf(RoleplayRealtimeService.ReviewCompletedEvent),
                Does.Not.Contain(BystanderId));
            Assert.That(guildWide!.GetType().GetProperty("Reason"), Is.Null,
                "reviewer feedback is for the character's players, not for the room");
        });
    }

    [Test]
    public async Task ReviewCompleted_OnApproval_SaysTheCharacterMayBePlayed()
    {
        await SeedAsync();
        await AddPersonaAsync(PersonaId, "Mayor Cogsgrove", PlayerId, PersonaApprovalState.Submitted);

        await _profiles.ApproveAsync(GuildId, PersonaId,
            _permissions, _personas, _pages, _auditLog, _realtime, _context,
            TestPrincipal.Create(ReviewerId));

        var payload = PayloadOf(RoleplayRealtimeService.ReviewCompletedEvent);

        Assert.Multiple(() =>
        {
            Assert.That(Field<bool?>(payload, "Approved"), Is.True);
            Assert.That(Field<bool?>(payload, "CanSpeak"), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Grants and autoproxy
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GrantCreated_ReachesTheGranteeAndTheManagersOnly()
    {
        await SeedAsync();
        await AddPersonaAsync(GuildPersonaId, "Narrator", ownerUserId: null);

        await _endpoint.CreateGrantAsync(GuildId, GuildPersonaId,
            new CreatePersonaGrantDto { UserId = PlayerId },
            _permissions, _personas, _auditLog, _realtime, _context,
            TestPrincipal.Create(ReviewerId));

        Assert.That(
            RecipientsOf(RoleplayRealtimeService.GrantCreatedEvent),
            Is.EquivalentTo(new[] { OwnerId, ReviewerId, PlayerId }),
            "who may speak as a character is what the gated grant list answers");
    }

    [Test]
    public async Task AutoproxyChanged_ReachesTheCallerAlone()
    {
        await SeedAsync();
        await AddPersonaAsync(PersonaId, "Mayor Cogsgrove", PlayerId, PersonaApprovalState.Approved);

        await _profiles.SetAutoproxyAsync(GuildId, ChannelId,
            new SetAutoproxyDto { Mode = AutoproxyMode.Pinned, PersonaId = PersonaId },
            _permissions, _personas, _realtime, _context, TestPrincipal.Create(PlayerId));

        var payload = PayloadOf(RoleplayRealtimeService.AutoproxyChangedEvent);

        Assert.Multiple(() =>
        {
            Assert.That(
                RecipientsOf(RoleplayRealtimeService.AutoproxyChangedEvent),
                Is.EqualTo(new[] { PlayerId }));
            Assert.That(Field<string>(payload, "PersonaId"), Is.EqualTo(PersonaId));
            Assert.That(Field<AutoproxyMode?>(payload, "Mode"), Is.EqualTo(AutoproxyMode.Pinned));
        });
    }

    [Test]
    public async Task PersonaUnadopted_TellsTheGuildToStopRenderingIt()
    {
        await SeedAsync();
        await AddPersonaAsync(PersonaId, "Mayor Cogsgrove", PlayerId);

        await _profiles.DeleteAsync(GuildId, PersonaId,
            _permissions, _personas, _realtime, _context, TestPrincipal.Create(PlayerId));

        Assert.That(
            RecipientsOf(RoleplayRealtimeService.PersonaUnadoptedEvent),
            Does.Contain(BystanderId));
    }
}
