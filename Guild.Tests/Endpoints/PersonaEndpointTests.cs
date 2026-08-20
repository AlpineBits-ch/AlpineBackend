using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Endpoints.Persona;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// The persona routes, concentrating on the impersonation controls: length and avatar caps, the
/// display-name collision against names a member already trusts, and proxy-prefix uniqueness across
/// both tables it spans.
/// </summary>
[TestFixture]
public class PersonaEndpointTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "chan-1";
    private const string UserId = "user-1";
    private const string MemberId = "member-1";
    private const string RoleId = "role-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
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
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _personas = new PersonaService(_cache, _context);
        _pages = new PersonaPageService(_context);
        _displayGuard = new PersonaDisplayGuard(_context);
        _auditLog = new AuditLogService(_context);
        _realtime = RoleplayTestFactory.CreateRealtime(
            _context, _permissions, _personas, new FakeHubContext());
        _endpoint = new PersonaEndpoint();
        _profiles = new PersonaProfileEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedGuildAsync(
        ModulePermissions module = ModulePermissions.UsePersonas,
        GuildFeatures features = GuildFeatures.Personas | GuildFeatures.Wiki)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner-1", Name = "Blackwater", Features = features,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "Players",
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            ModulePermissions = module,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "the-inn", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    private async Task<Persona> SeedOwnPersonaAsync(string id, string name, string? prefix = null,
        bool adopted = true, bool hasSpoken = false,
        PersonaApprovalState approval = PersonaApprovalState.Approved)
    {
        _context.Set<Persona>().Add(new Persona
        {
            Id = id, Scope = PersonaScope.User, OwnerUserId = UserId, Name = name, HasSpoken = hasSpoken,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        if (adopted)
        {
            _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
            {
                Id = $"profile-{id}", PersonaId = id, GuildId = GuildId, ProxyPrefix = prefix,
                ApprovalState = approval,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
        return (await _context.Set<Persona>().FindAsync(id))!;
    }

    private Task<IResult> UpsertProfileAsync(string personaId, UpsertPersonaProfileDto dto) =>
        _profiles.UpsertAsync(GuildId, personaId, dto, _permissions, _personas, _displayGuard, _pages,
            _realtime, _context, TestPrincipal.Create(UserId));

    private Task<IResult> CreateGuildPersonaAsync(CreatePersonaDto dto) =>
        _endpoint.CreateForGuildAsync(GuildId, dto, _permissions, _personas, _displayGuard, _auditLog,
            _pages, _realtime, _context, TestPrincipal.Create(UserId));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The guild's cast

    [Test]
    public async Task ListCast_NamesEverybodysCharactersAndNobodysPlayer()
    {
        await SeedGuildAsync();
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove");

        // Somebody else's character: the one thing the caller's own persona list cannot name, and
        // the one a scene's turn order is full of.
        _context.Set<Persona>().Add(new Persona
        {
            Id = "guard", Scope = PersonaScope.User, OwnerUserId = "user-2", Name = "Town Guard",
            Color = "#4F8A6B", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = "profile-guard", PersonaId = "guard", GuildId = GuildId, DisplayName = "The Guard",
            ApprovalState = PersonaApprovalState.Approved,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();

        var result = await _endpoint.ListCastAsync(
            GuildId, _permissions, new PersonaCastService(_context), _context,
            TestPrincipal.Create(UserId));

        var cast = (result as Ok<List<PersonaCastMemberDto>>)?.Value;
        var serialized = System.Text.Json.JsonSerializer.Serialize(cast);

        Assert.That(cast, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(cast!.Select(c => c.PersonaId), Is.EquivalentTo(new[] { "mayor", "guard" }));
            Assert.That(cast.Single(c => c.PersonaId == "guard").Name, Is.EqualTo("The Guard"),
                "the per-guild override is what that guild reads");
            Assert.That(cast.Single(c => c.PersonaId == "guard").Color, Is.EqualTo("#4F8A6B"));
            Assert.That(serialized, Does.Not.Contain("user-2"),
                "a cast list must not say who is behind a character");
        });
    }

    [Test]
    public async Task ListCast_WithoutThePersonasModule_IsForbidden()
    {
        await SeedGuildAsync(features: GuildFeatures.Wiki);

        var result = await _endpoint.ListCastAsync(
            GuildId, _permissions, new PersonaCastService(_context), _context,
            TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Account-level personas

    [Test]
    public async Task CreateOwn_Valid_PersistsThePersona()
    {
        var result = await _endpoint.CreateOwnAsync(
            new CreatePersonaDto { Name = "Mayor Cogsgrove", Pronouns = "he/him" },
            _realtime, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<PersonaDto>>());
            Assert.That(_context.Set<Persona>().Count(p => p.OwnerUserId == UserId), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CreateOwn_AvatarOffThisInstance_ReturnsBadRequest()
    {
        var result = await _endpoint.CreateOwnAsync(
            new CreatePersonaDto { Name = "Mayor", AvatarUrl = "https://tracker.example/pixel.png" },
            _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>(),
            "an arbitrary host is a tracking pixel on every message the character sends");
    }

    [Test]
    public async Task CreateOwn_OversizeName_ReturnsBadRequest()
    {
        var result = await _endpoint.CreateOwnAsync(
            new CreatePersonaDto { Name = new string('x', PersonaLimits.MaxNameLength + 1) },
            _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task DeleteOwn_PersonaThatHasSpoken_RetiresInsteadAndSaysSo()
    {
        await SeedGuildAsync();
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", hasSpoken: true);

        var result = await _endpoint.DeleteOwnAsync("mayor", _personas, _realtime, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.Set<Persona>().FindAsync("mayor");

        Assert.Multiple(() =>
        {
            Assert.That(((Ok<PersonaDeletionDto>)result).Value!.Retired, Is.True);
            Assert.That(stored, Is.Not.Null, "Message.PersonaId must never dangle");
            Assert.That(stored!.IsRetired, Is.True);
        });
    }

    [Test]
    public async Task DeleteOwn_PersonaThatNeverSpoke_IsRemoved()
    {
        await SeedGuildAsync();
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove");

        await _endpoint.DeleteOwnAsync("mayor", _personas, _realtime, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(await _context.Set<Persona>().FindAsync("mayor"), Is.Null);
    }

    [Test]
    public async Task GetOwn_SomebodyElsesPersona_ReturnsNotFound()
    {
        _context.Set<Persona>().Add(new Persona
        {
            Id = "theirs", Scope = PersonaScope.User, OwnerUserId = "somebody-else", Name = "Theirs",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetOwnAsync("theirs", _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Impersonation controls

    [Test]
    public async Task UpsertProfile_DisplayNameMatchingARoleName_ReturnsConflict()
    {
        await SeedGuildAsync();
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", adopted: false);

        var result = await UpsertProfileAsync("mayor", new UpsertPersonaProfileDto { DisplayName = "Players" });

        Assert.That(result, Is.InstanceOf<Conflict<string>>());
    }

    [Test]
    public async Task UpsertProfile_DisplayNameMatchingAMemberNickname_ReturnsConflict()
    {
        await SeedGuildAsync();
        var member = await _context.GuildMembers.FindAsync(MemberId);
        member!.Nickname = "Server Owner";
        await _context.SaveChangesAsync();

        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", adopted: false);

        var result = await UpsertProfileAsync("mayor", new UpsertPersonaProfileDto { DisplayName = "server owner" });

        Assert.That(result, Is.InstanceOf<Conflict<string>>(), "the check is case-insensitive");
    }

    [Test]
    public async Task UpsertProfile_ProxyPrefixAlreadyTaken_ReturnsConflictNamingTheOther()
    {
        await SeedGuildAsync();
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "M:");
        await SeedOwnPersonaAsync("miller", "The Miller", adopted: false);

        var result = await UpsertProfileAsync("miller", new UpsertPersonaProfileDto { ProxyPrefix = "M:" });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Conflict<string>>());
            Assert.That(((Conflict<string>)result).Value, Does.Contain("Mayor Cogsgrove"),
                "the caller has to be told which character they would have been mistaken for");
        });
    }

    [Test]
    public async Task UpsertProfile_FreePrefix_Adopts()
    {
        await SeedGuildAsync();
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", adopted: false);

        var result = await UpsertProfileAsync("mayor", new UpsertPersonaProfileDto { ProxyPrefix = "M:" });
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<PersonaGuildProfileDto>>());
            Assert.That(_context.Set<PersonaGuildProfile>().Count(p => p.PersonaId == "mayor"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task UpsertProfile_SomebodyElsesPersona_ReturnsForbid()
    {
        await SeedGuildAsync();
        _context.Set<Persona>().Add(new Persona
        {
            Id = "theirs", Scope = PersonaScope.User, OwnerUserId = "somebody-else", Name = "Theirs",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await UpsertProfileAsync("theirs", new UpsertPersonaProfileDto());

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpsertProfile_WithoutUsePersonas_ReturnsForbid()
    {
        await SeedGuildAsync(module: ModulePermissions.None);
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", adopted: false);

        var result = await UpsertProfileAsync("mayor", new UpsertPersonaProfileDto());

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpsertProfile_ModuleSwitchedOff_ReturnsForbid()
    {
        await SeedGuildAsync(features: GuildFeatures.Wiki);
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", adopted: false);

        var result = await UpsertProfileAsync("mayor", new UpsertPersonaProfileDto());

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Guild personas and grants

    [Test]
    public async Task CreateForGuild_WithoutManageAnyPersona_ReturnsForbid()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas);

        var result = await CreateGuildPersonaAsync(new CreatePersonaDto { Name = "Narrator" });

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateForGuild_AdoptsIntoItsOwnGuildAndAudits()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas | ModulePermissions.ManageAnyPersona);

        var result = await CreateGuildPersonaAsync(new CreatePersonaDto { Name = "Narrator" });
        await _context.SaveChangesAsync();

        var persona = _context.Set<Persona>().Single(p => p.Scope == PersonaScope.Guild);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<PersonaGuildProfileDto>>());
            Assert.That(_context.Set<PersonaGuildProfile>().Count(p => p.PersonaId == persona.Id), Is.EqualTo(1),
                "the profile is where its prefix and approval live, so it is unusable without one");
            Assert.That(_context.Set<GuildAuditLogEntry>()
                .Count(e => e.ActionType == AuditActionType.PersonaCreated), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CreateForGuild_NameMatchingARoleName_ReturnsConflict()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas | ModulePermissions.ManageAnyPersona);

        var result = await CreateGuildPersonaAsync(new CreatePersonaDto { Name = "Players" });

        Assert.That(result, Is.InstanceOf<Conflict<string>>());
    }

    [Test]
    public async Task CreateGrant_WithBothRoleAndUser_ReturnsBadRequest()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas | ModulePermissions.ManageAnyPersona);
        await SeedGuildPersonaAsync("narrator", "Narrator");

        var result = await _endpoint.CreateGrantAsync(GuildId, "narrator",
            new CreatePersonaGrantDto { RoleId = RoleId, UserId = UserId },
            _permissions, _personas, _auditLog, _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateGrant_ThatWouldMakeAPrefixAmbiguous_ReturnsConflict()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas | ModulePermissions.ManageAnyPersona);
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", prefix: "N:");
        await SeedGuildPersonaAsync("narrator", "Narrator", prefix: "N:");

        var result = await _endpoint.CreateGrantAsync(GuildId, "narrator",
            new CreatePersonaGrantDto { UserId = UserId },
            _permissions, _personas, _auditLog, _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Conflict<string>>(),
            "widening who can reach a persona introduces a collision without any prefix being edited");
    }

    [Test]
    public async Task CreateGrant_ForAStranger_ReturnsBadRequest()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas | ModulePermissions.ManageAnyPersona);
        await SeedGuildPersonaAsync("narrator", "Narrator");

        var result = await _endpoint.CreateGrantAsync(GuildId, "narrator",
            new CreatePersonaGrantDto { UserId = "not-a-member" },
            _permissions, _personas, _auditLog, _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Autoproxy

    [Test]
    public async Task SetAutoproxy_PinnedWithoutAPersona_ReturnsBadRequest()
    {
        await SeedGuildAsync();

        var result = await _profiles.SetAutoproxyAsync(GuildId, ChannelId,
            new SetAutoproxyDto { Mode = AutoproxyMode.Pinned },
            _permissions, _personas, _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task SetAutoproxy_PinnedOntoAPersonaTheCallerCannotUse_ReturnsForbid()
    {
        await SeedGuildAsync();
        await SeedGuildPersonaAsync("narrator", "Narrator");

        var result = await _profiles.SetAutoproxyAsync(GuildId, ChannelId,
            new SetAutoproxyDto { Mode = AutoproxyMode.Pinned, PersonaId = "narrator" },
            _permissions, _personas, _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task SetAutoproxy_Off_ClearsTheRememberedPersona()
    {
        await SeedGuildAsync();
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove");

        await _profiles.SetAutoproxyAsync(GuildId, ChannelId,
            new SetAutoproxyDto { Mode = AutoproxyMode.Pinned, PersonaId = "mayor" },
            _permissions, _personas, _realtime, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        await _profiles.SetAutoproxyAsync(GuildId, ChannelId,
            new SetAutoproxyDto { Mode = AutoproxyMode.Off },
            _permissions, _personas, _realtime, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var state = _context.Set<PersonaAutoproxyState>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(state.Mode, Is.EqualTo(AutoproxyMode.Off));
            Assert.That(state.PersonaId, Is.Null);
        });
    }

    [Test]
    public async Task SetAutoproxy_ChannelFromAnotherGuild_ReturnsNotFound()
    {
        await SeedGuildAsync();
        _context.Channels.Add(new Channel
        {
            Id = "chan-elsewhere", GuildId = "other-guild", Name = "nope", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await _profiles.SetAutoproxyAsync(GuildId, "chan-elsewhere",
            new SetAutoproxyDto { Mode = AutoproxyMode.Off },
            _permissions, _personas, _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Approval

    [Test]
    public async Task Submit_AnApprovedProfile_ReturnsConflict()
    {
        await SeedGuildAsync();
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove");

        var result = await _profiles.SubmitAsync(GuildId, "mayor", _permissions, _personas, _pages,
            _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Conflict<string>>(),
            "an approved character stays approved through an edit - the pending revisions are the review");
    }

    [Test]
    public async Task Approve_WithoutApprovePersonas_ReturnsForbid()
    {
        await SeedGuildAsync();
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove");

        var result = await _profiles.ApproveAsync(GuildId, "mayor", _permissions, _personas, _pages,
            _auditLog, _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Approve_RecordsTheReviewerAndAudits()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas | ModulePermissions.ApprovePersonas);
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", approval: PersonaApprovalState.Submitted);

        var result = await _profiles.ApproveAsync(GuildId, "mayor", _permissions, _personas, _pages,
            _auditLog, _realtime, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var profile = _context.Set<PersonaGuildProfile>().Single(p => p.PersonaId == "mayor");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<PersonaGuildProfileDto>>());
            Assert.That(profile.ApprovalState, Is.EqualTo(PersonaApprovalState.Approved));
            Assert.That(profile.ApprovedByUserId, Is.EqualTo(UserId));
            Assert.That(_context.Set<GuildAuditLogEntry>()
                .Count(e => e.ActionType == AuditActionType.PersonaApproved), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RequestChanges_WithoutAReason_ReturnsBadRequest()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas | ModulePermissions.ApprovePersonas);
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove");

        var result = await _profiles.RequestChangesAsync(GuildId, "mayor",
            new RequestPersonaChangesDto { Reason = "   " },
            _permissions, _personas, _pages, _auditLog, _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Approve_ANeverSubmittedProfile_ReturnsConflict()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas | ModulePermissions.ApprovePersonas);
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", approval: PersonaApprovalState.Draft);

        var result = await _profiles.ApproveAsync(GuildId, "mayor", _permissions, _personas, _pages,
            _auditLog, _realtime, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Conflict<string>>());
    }

    [Test]
    public async Task RequestChanges_OnASubmittedProfile_RecordsTheReasonAndAudits()
    {
        await SeedGuildAsync(module: ModulePermissions.UsePersonas | ModulePermissions.ApprovePersonas);
        await SeedOwnPersonaAsync("mayor", "Mayor Cogsgrove", approval: PersonaApprovalState.Submitted);

        await _profiles.RequestChangesAsync(GuildId, "mayor",
            new RequestPersonaChangesDto { Reason = "The backstory contradicts the setting." },
            _permissions, _personas, _pages, _auditLog, _realtime, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var profile = _context.Set<PersonaGuildProfile>().Single(p => p.PersonaId == "mayor");

        Assert.Multiple(() =>
        {
            Assert.That(profile.ApprovalState, Is.EqualTo(PersonaApprovalState.ChangesRequested));
            Assert.That(profile.ChangesRequestedReason, Does.Contain("contradicts"));
            Assert.That(_context.Set<GuildAuditLogEntry>()
                .Count(e => e.ActionType == AuditActionType.PersonaRejected), Is.EqualTo(1));
        });
    }

    private async Task SeedGuildPersonaAsync(string id, string name, string? prefix = null)
    {
        _context.Set<Persona>().Add(new Persona
        {
            Id = id, Scope = PersonaScope.Guild, OwnerGuildId = GuildId, Name = name,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
        {
            Id = $"profile-{id}", PersonaId = id, GuildId = GuildId, ProxyPrefix = prefix,
            ApprovalState = PersonaApprovalState.Approved,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }
}
