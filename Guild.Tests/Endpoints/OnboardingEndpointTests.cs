using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers OnboardingEndpoint: admin config GET/PUT (ManageGuild-gated), the member-facing status
/// GET, and the accept action - including that accepting invalidates the user's cached permissions
/// (onboarding can grant/gate visibility of default channels going forward).
/// </summary>
[TestFixture]
public class OnboardingEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private FakeMessageBus _bus = null!;
    private OnboardingValidationService _validation = null!;
    private OnboardingGrantService _grantService = null!;
    private OnboardingConfigService _configService = null!;
    private OnboardingEndpoint _endpoint = null!;

    /// <summary>Accepting a guild that has no prompts configured - the common case, and what every
    /// pre-prompts client sends.</summary>
    private static OnboardingResponsesDto NoResponses => new();

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _bus = new FakeMessageBus();
        _validation = new OnboardingValidationService(_context, _permissionService);
        _grantService = new OnboardingGrantService(_context, _validation);
        _configService = new OnboardingConfigService(_context, _validation, _permissionService, _auditLog);
        _endpoint = new OnboardingEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<GuildMember> SeedManagerMember(int rolePosition = 0)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageGuild, Position = rolePosition, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var member = new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" };
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
        return member;
    }

    /// <summary>
    /// GuildMember.OnboardingCompletedAt defaults to "now" at the property declaration (members
    /// are onboarded-by-default unless a join flow explicitly nulls it out for a guild that has
    /// onboarding configured) - so tests exercising the "hasn't accepted yet" path must null it
    /// out explicitly, which this helper does by default.
    /// </summary>
    private async Task<GuildMember> SeedPlainMember(bool onboardingCompleted = false)
    {
        _context.Guilds.Add(MakeGuild());
        var member = new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}",
            OnboardingCompletedAt = onboardingCompleted ? DateTimeOffset.UtcNow : null,
        };
        _context.GuildMembers.Add(member);
        await _context.SaveChangesAsync();
        return member;
    }

    // ══════════════════════════════════════════════════════════════════════ GetConfig (admin)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetConfig_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.GetConfig(GuildId, _permissionService, _configService, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetConfig_LacksManageGuild_ReturnsForbid()
    {
        await SeedPlainMember();
        var result = await _endpoint.GetConfig(GuildId, _permissionService, _configService, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetConfig_NoRowYet_ReturnsDisabledDefaults()
    {
        await SeedManagerMember();
        var result = await _endpoint.GetConfig(GuildId, _permissionService, _configService, TestPrincipal.Create(UserId));

        var ok = result as Ok<UpdateOnboardingConfigDto>;
        Assert.That(ok!.Value!.Enabled, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════ UpdateConfig (admin)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateConfig_EnabledWithoutRulesText_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var dto = new UpdateOnboardingConfigDto { Enabled = true, RulesText = null };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_EnabledWithRulesText_CreatesConfig()
    {
        await SeedManagerMember();
        _context.Channels.Add(new Channel
        {
            Id = "chan-1", GuildId = GuildId, Name = "general", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var dto = new UpdateOnboardingConfigDto { Enabled = true, RulesText = "Be nice", DefaultChannelIds = ["chan-1"] };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<UpdateOnboardingConfigDto>>());
        var config = _context.Set<GuildOnboardingConfig>().Find(GuildId);
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.RulesText, Is.EqualTo("Be nice"));
    }

    [Test]
    public async Task UpdateConfig_DisabledWithoutRulesText_IsAllowed()
    {
        await SeedManagerMember();
        var dto = new UpdateOnboardingConfigDto { Enabled = false, RulesText = null };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<UpdateOnboardingConfigDto>>());
    }

    [Test]
    public async Task UpdateConfig_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();
        await _endpoint.UpdateConfig(GuildId, new UpdateOnboardingConfigDto { Enabled = false }, _permissionService, _configService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.GuildId == GuildId).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].ActionType, Is.EqualTo(AuditActionType.OnboardingConfigUpdated));
    }

    // ══════════════════════════════════════════════════════════════════════ GetMyStatus
    // (member-facing) ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetMyStatus_NotAMember_ReturnsNotFound()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetMyStatus(GuildId, _context, _configService, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetMyStatus_NeverAccepted_ReportsNotCompleted()
    {
        await SeedPlainMember();
        _context.Set<GuildOnboardingConfig>().Add(new GuildOnboardingConfig { GuildId = GuildId, Enabled = true, RulesText = "Rules", UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetMyStatus(GuildId, _context, _configService, TestPrincipal.Create(UserId));

        var value = ((IValueHttpResult)result).Value!;
        Assert.That((bool)value.GetType().GetProperty("completed")!.GetValue(value)!, Is.False);
        Assert.That((string)value.GetType().GetProperty("rulesText")!.GetValue(value)!, Is.EqualTo("Rules"));
    }

    [Test]
    public async Task GetMyStatus_AlreadyAccepted_ReportsCompleted()
    {
        var member = await SeedPlainMember();
        member.OnboardingCompletedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetMyStatus(GuildId, _context, _configService, TestPrincipal.Create(UserId));

        var value = ((IValueHttpResult)result).Value!;
        Assert.That((bool)value.GetType().GetProperty("completed")!.GetValue(value)!, Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ Accept
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Accept_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.Accept(GuildId, NoResponses, _context, TestPrincipal.CreateAnonymous(), _permissionService, _grantService, _bus);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Accept_NotAMember_ReturnsNotFound()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _endpoint.Accept(GuildId, NoResponses, _context, TestPrincipal.Create(UserId), _permissionService, _grantService, _bus);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task Accept_FirstTime_SetsOnboardingCompletedAt()
    {
        await SeedPlainMember();

        var result = await _endpoint.Accept(GuildId, NoResponses, _context, TestPrincipal.Create(UserId), _permissionService, _grantService, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok>());
        var member = _context.GuildMembers.Find(MemberId);
        Assert.That(member!.OnboardingCompletedAt, Is.Not.Null);
    }

    [Test]
    public async Task Accept_FirstTime_InvalidatesPermissionCache()
    {
        await SeedPlainMember();
        var cacheKey = Guild.Application.Services.GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(cacheKey, "stale-cached-permissions");

        await _endpoint.Accept(GuildId, NoResponses, _context, TestPrincipal.Create(UserId), _permissionService, _grantService, _bus);

        Assert.That(_cache.HasEntry(cacheKey), Is.False, "Accepting onboarding must invalidate the user's cached permissions");
    }

    [Test]
    public async Task Accept_AlreadyAccepted_DoesNotChangeTimestamp()
    {
        var member = await SeedPlainMember();
        var originalTimestamp = DateTimeOffset.UtcNow.AddDays(-1);
        member.OnboardingCompletedAt = originalTimestamp;
        await _context.SaveChangesAsync();

        await _endpoint.Accept(GuildId, NoResponses, _context, TestPrincipal.Create(UserId), _permissionService, _grantService, _bus);
        await _context.SaveChangesAsync();

        var reloaded = _context.GuildMembers.Find(MemberId);
        Assert.That(reloaded!.OnboardingCompletedAt, Is.EqualTo(originalTimestamp));
    }

    [Test]
    public async Task Accept_FirstTime_PublishesMemberUpdatedForBots()
    {
        await SeedPlainMember();

        await _endpoint.Accept(GuildId, NoResponses, _context, TestPrincipal.Create(UserId), _permissionService, _grantService, _bus);

        Assert.That(_bus.Published.OfType<MemberUpdatedForBots>().Count(), Is.EqualTo(1),
            "completing onboarding is Discord's GUILD_MEMBER_UPDATE with pending: false");
    }

    [Test]
    public async Task Accept_SecondTime_DoesNotRepublish()
    {
        await SeedPlainMember();

        await _endpoint.Accept(GuildId, NoResponses, _context, TestPrincipal.Create(UserId), _permissionService, _grantService, _bus);
        await _context.SaveChangesAsync();
        await _endpoint.Accept(GuildId, NoResponses, _context, TestPrincipal.Create(UserId), _permissionService, _grantService, _bus);

        Assert.That(_bus.Published.OfType<MemberUpdatedForBots>().Count(), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ Validation and the
    // disable path ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateConfig_UnknownDefaultChannel_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var dto = new UpdateOnboardingConfigDto { Enabled = true, RulesText = "Rules", DefaultChannelIds = ["chan-nope"] };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_ChannelFromAnotherGuild_ReturnsBadRequest()
    {
        await SeedManagerMember();
        _context.Channels.Add(new Channel
        {
            Id = "chan-other", GuildId = "some-other-guild", Name = "elsewhere", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var dto = new UpdateOnboardingConfigDto { Enabled = true, RulesText = "Rules", DefaultChannelIds = ["chan-other"] };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_KnownDefaultChannel_IsAccepted()
    {
        await SeedManagerMember();
        _context.Channels.Add(new Channel
        {
            Id = "chan-1", GuildId = GuildId, Name = "general", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var dto = new UpdateOnboardingConfigDto { Enabled = true, RulesText = "Rules", DefaultChannelIds = ["chan-1", "chan-1"] };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<UpdateOnboardingConfigDto>>());
        var config = _context.Set<GuildOnboardingConfig>().Find(GuildId);
        Assert.That(config!.DefaultChannelIds, Is.EqualTo(new[] { "chan-1" }), "duplicates are collapsed");
    }

    [Test]
    public async Task UpdateConfig_OversizeRulesText_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var dto = new UpdateOnboardingConfigDto { Enabled = true, RulesText = new string('x', 4001) };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_TooManyDefaultChannels_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var dto = new UpdateOnboardingConfigDto
        {
            Enabled = true, RulesText = "Rules",
            DefaultChannelIds = Enumerable.Range(0, 26).Select(i => $"chan-{i}").ToList(),
        };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_Disabling_CompletesPendingMembersAndInvalidatesTheirCache()
    {
        await SeedManagerMember();
        _context.Set<GuildOnboardingConfig>().Add(new GuildOnboardingConfig
        {
            GuildId = GuildId, Enabled = true, RulesText = "Rules", UpdatedAt = DateTimeOffset.UtcNow,
        });
        var pending = new GuildMember
        {
            Id = "member-pending", GuildId = GuildId, UserId = "user-pending", JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"user-pending#{GuildId}", OnboardingCompletedAt = null,
        };
        _context.GuildMembers.Add(pending);
        await _context.SaveChangesAsync();

        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, "user-pending");
        _cache.SetEntry(cacheKey, "stale-cached-permissions");

        await _endpoint.UpdateConfig(GuildId, new UpdateOnboardingConfigDto { Enabled = false },
            _permissionService, _configService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.GuildMembers.Find("member-pending")!.OnboardingCompletedAt, Is.Not.Null,
                "disabling onboarding must not strand pending members participation-restricted");
            Assert.That(_cache.HasEntry(cacheKey), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Prompts — config
    // reconciliation, role safety, and applying answers

    private const string GrantRoleId = "role-grant";
    private const string GrantChannelId = "chan-grant";

    private async Task SeedGrantTargets(Permissions rolePermissions = Permissions.None)
    {
        _context.Roles.Add(new Role
        {
            Id = GrantRoleId, GuildId = GuildId, Name = "Gamer", Position = 0, Permissions = rolePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = GrantChannelId, GuildId = GuildId, Name = "gaming", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private static UpdateOnboardingConfigDto ConfigWithPrompt(
        bool required = false, bool singleSelect = false, string? promptId = null, string? optionId = null,
        List<string>? roleIds = null, List<string>? channelIds = null) => new()
    {
        Enabled = true,
        RulesText = "Be nice",
        Prompts =
        [
            new OnboardingPromptDto
            {
                Id = promptId,
                Title = "What brings you here?",
                Type = OnboardingPromptType.MultipleChoice,
                SingleSelect = singleSelect,
                Required = required,
                InOnboarding = true,
                Options =
                [
                    new OnboardingPromptOptionDto
                    {
                        Id = optionId,
                        Title = "Gaming",
                        RoleIds = roleIds ?? [GrantRoleId],
                        ChannelIds = channelIds ?? [GrantChannelId],
                    },
                ],
            },
        ],
    };

    private async Task<UpdateOnboardingConfigDto> PutConfig(UpdateOnboardingConfigDto dto)
    {
        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<UpdateOnboardingConfigDto>>(),
            (result as BadRequest<string>)?.Value ?? "expected the config to be accepted");
        return ((Ok<UpdateOnboardingConfigDto>)result).Value!;
    }

    [Test]
    public async Task UpdateConfig_NewPrompt_IsCreatedWithGeneratedIds()
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets();

        var saved = await PutConfig(ConfigWithPrompt());

        Assert.Multiple(() =>
        {
            Assert.That(saved.Prompts, Has.Count.EqualTo(1));
            Assert.That(saved.Prompts[0].Id, Does.StartWith("onbp_"));
            Assert.That(saved.Prompts[0].Options[0].Id, Does.StartWith("onbo_"));
            Assert.That(_context.Set<GuildOnboardingPrompt>().Count(), Is.EqualTo(1));
            Assert.That(_context.Set<GuildOnboardingPromptOption>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task UpdateConfig_ExistingPromptId_UpdatesInPlace()
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets();
        var saved = await PutConfig(ConfigWithPrompt());
        var promptId = saved.Prompts[0].Id;
        var optionId = saved.Prompts[0].Options[0].Id;

        var edited = ConfigWithPrompt(promptId: promptId, optionId: optionId);
        edited.Prompts[0].Title = "Why are you here?";
        var reSaved = await PutConfig(edited);

        Assert.Multiple(() =>
        {
            Assert.That(reSaved.Prompts[0].Id, Is.EqualTo(promptId), "editing must not re-create the prompt");
            Assert.That(reSaved.Prompts[0].Options[0].Id, Is.EqualTo(optionId));
            Assert.That(_context.Set<GuildOnboardingPrompt>().Single().Title, Is.EqualTo("Why are you here?"));
        });
    }

    [Test]
    public async Task UpdateConfig_OmittedPrompt_IsDeleted()
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets();
        await PutConfig(ConfigWithPrompt());

        await PutConfig(new UpdateOnboardingConfigDto { Enabled = true, RulesText = "Be nice" });

        Assert.Multiple(() =>
        {
            Assert.That(_context.Set<GuildOnboardingPrompt>().Count(), Is.Zero);
            Assert.That(_context.Set<GuildOnboardingPromptOption>().Count(), Is.Zero, "options cascade with their prompt");
        });
    }

    [Test]
    public async Task UpdateConfig_PromptChanges_AreAudited()
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets();
        await PutConfig(ConfigWithPrompt());

        var entries = _context.Set<GuildAuditLogEntry>().Select(e => e.ActionType).ToList();

        Assert.That(entries, Does.Contain(AuditActionType.OnboardingPromptCreated));
    }

    [Test]
    public async Task UpdateConfig_PrivilegedRoleInOption_ReturnsBadRequest()
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets(rolePermissions: Permissions.BanMembers);

        var result = await _endpoint.UpdateConfig(GuildId, ConfigWithPrompt(), _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>(),
            "a self-service option must never hand out moderation permissions");
    }

    [Test]
    public async Task UpdateConfig_RoleAtOrAboveActorsHighest_ReturnsBadRequest()
    {
        await SeedManagerMember(rolePosition: 10);
        _context.Roles.Add(new Role
        {
            Id = GrantRoleId, GuildId = GuildId, Name = "Above", Position = 20, Permissions = Permissions.None,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var dto = ConfigWithPrompt(channelIds: []);
        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_OptionGrantingNothing_ReturnsBadRequest()
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets();

        var dto = ConfigWithPrompt(roleIds: [], channelIds: []);
        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_PromptWithNoOptions_ReturnsBadRequest()
    {
        await SeedManagerMember(rolePosition: 10);

        var dto = new UpdateOnboardingConfigDto
        {
            Enabled = true, RulesText = "Be nice",
            Prompts = [new OnboardingPromptDto { Title = "Empty", Options = [] }],
        };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_EnabledWithPromptButNoRulesText_IsAllowed()
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets();

        var dto = ConfigWithPrompt();
        dto.RulesText = null;

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _configService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<UpdateOnboardingConfigDto>>(),
            "prompts alone are enough to justify enabling onboarding");
    }

    // ── applying answers ────────────────────────────────────────────────────

    /// <summary>Seeds a configured prompt plus a separate pending member to answer it, and returns
    /// (promptId, optionId).</summary>
    private async Task<(string PromptId, string OptionId, GuildMember Member)> SeedAnswerableGuild(
        bool required = false, bool singleSelect = false)
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets();
        var saved = await PutConfig(ConfigWithPrompt(required: required, singleSelect: singleSelect));

        var member = new GuildMember
        {
            Id = "member-joiner", GuildId = GuildId, UserId = "user-joiner", JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"user-joiner#{GuildId}", OnboardingCompletedAt = null,
        };
        _context.GuildMembers.Add(member);
        await _context.SaveChangesAsync();

        return (saved.Prompts[0].Id!, saved.Prompts[0].Options[0].Id!, member);
    }

    private async Task<IResult> AcceptAs(GuildMember member, OnboardingResponsesDto dto) =>
        await _endpoint.Accept(GuildId, dto, _context, TestPrincipal.Create(member.UserId), _permissionService,
            _grantService, _bus);

    [Test]
    public async Task Accept_WithAnswer_GrantsRoleAndChannelVisibility()
    {
        var (promptId, optionId, member) = await SeedAnswerableGuild();

        var result = await AcceptAs(member, new OnboardingResponsesDto
        {
            Responses = [new OnboardingPromptResponseDto { PromptId = promptId, OptionIds = [optionId] }],
        });
        await _context.SaveChangesAsync();

        var overwrite = _context.Set<ChannelPermission>()
            .SingleOrDefault(p => p.MemberId == member.Id && p.ChannelId == GrantChannelId);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok>());
            Assert.That(_context.RoleMembers.Any(rm => rm.MemberId == member.Id && rm.RoleId == GrantRoleId), Is.True);
            Assert.That(overwrite, Is.Not.Null);
            Assert.That(overwrite!.AllowPermissions, Is.EqualTo(Permissions.ViewChannel));
            Assert.That(_context.Set<GuildOnboardingGrant>().Count(g => g.MemberId == member.Id), Is.EqualTo(2),
                "one grant row per role and per channel, so revocation knows exactly what to take back");
            Assert.That(_context.Set<GuildMemberOnboardingResponse>().Count(r => r.MemberId == member.Id), Is.EqualTo(1));
            Assert.That(_context.GuildMembers.Find(member.Id)!.OnboardingCompletedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Accept_MissingRequiredPrompt_ReturnsBadRequest()
    {
        var (_, _, member) = await SeedAnswerableGuild(required: true);

        var result = await AcceptAs(member, new OnboardingResponsesDto());

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(_context.GuildMembers.Find(member.Id)!.OnboardingCompletedAt, Is.Null,
                "a rejected accept must leave the member pending");
        });
    }

    [Test]
    public async Task Accept_MultipleOptionsOnSingleSelectPrompt_ReturnsBadRequest()
    {
        var (promptId, optionId, member) = await SeedAnswerableGuild(singleSelect: true);

        var result = await AcceptAs(member, new OnboardingResponsesDto
        {
            Responses = [new OnboardingPromptResponseDto { PromptId = promptId, OptionIds = [optionId, "onbo_other"] }],
        });

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Accept_OptionFromAnotherPrompt_ReturnsBadRequest()
    {
        var (promptId, _, member) = await SeedAnswerableGuild();

        var result = await AcceptAs(member, new OnboardingResponsesDto
        {
            Responses = [new OnboardingPromptResponseDto { PromptId = promptId, OptionIds = ["onbo_bogus"] }],
        });

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Accept_RoleThatBecamePrivilegedAfterConfig_IsSkippedButChannelStillGranted()
    {
        var (promptId, optionId, member) = await SeedAnswerableGuild();

        // The admin wired up a harmless role, then someone later gave that role ban powers.
        var role = _context.Roles.Find(GrantRoleId)!;
        role.Permissions = Permissions.BanMembers;
        await _context.SaveChangesAsync();

        var result = await AcceptAs(member, new OnboardingResponsesDto
        {
            Responses = [new OnboardingPromptResponseDto { PromptId = promptId, OptionIds = [optionId] }],
        });
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok>());
            Assert.That(_context.RoleMembers.Any(rm => rm.MemberId == member.Id && rm.RoleId == GrantRoleId), Is.False,
                "config-time validation is not enough - the role must be re-checked when it is actually handed out");
            Assert.That(_context.Set<ChannelPermission>().Any(p => p.MemberId == member.Id), Is.True,
                "the rest of the option still applies");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Channels & Roles — re-answering after joining
    // ══════════════════════════════════════════════════════════════════════

    private async Task<IResult> SetResponsesAs(GuildMember member, OnboardingResponsesDto dto) =>
        await _endpoint.UpdateMyResponses(GuildId, dto, _context, TestPrincipal.Create(member.UserId),
            _permissionService, _grantService, _bus);

    private static OnboardingResponsesDto Pick(string promptId, params string[] optionIds) => new()
    {
        Responses = [new OnboardingPromptResponseDto { PromptId = promptId, OptionIds = optionIds.ToList() }],
    };

    [Test]
    public async Task GetMyPrompts_MarksCurrentSelection()
    {
        var (promptId, optionId, member) = await SeedAnswerableGuild();
        await AcceptAs(member, Pick(promptId, optionId));
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetMyPrompts(GuildId, _context, _configService, TestPrincipal.Create(member.UserId));

        var prompts = (List<MemberOnboardingPromptDto>)((IValueHttpResult)result).Value!;
        Assert.That(prompts.Single().Options.Single().Selected, Is.True);
    }

    [Test]
    public async Task UpdateMyResponses_Deselecting_RevokesRoleAndOverwrite()
    {
        var (promptId, optionId, member) = await SeedAnswerableGuild();
        await AcceptAs(member, Pick(promptId, optionId));
        await _context.SaveChangesAsync();

        await SetResponsesAs(member, Pick(promptId));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.RoleMembers.Any(rm => rm.MemberId == member.Id && rm.RoleId == GrantRoleId), Is.False);
            Assert.That(_context.Set<ChannelPermission>().Any(p => p.MemberId == member.Id), Is.False);
            Assert.That(_context.Set<GuildOnboardingGrant>().Any(g => g.MemberId == member.Id), Is.False);
            Assert.That(_context.Set<GuildMemberOnboardingResponse>().Any(r => r.MemberId == member.Id), Is.False);
        });
    }

    [Test]
    public async Task UpdateMyResponses_Deselecting_LeavesManuallyAssignedRoleAlone()
    {
        // The moderator handed this member the same role by hand.
        var (promptId, optionId, member) = await SeedAnswerableGuild();
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-manual", RoleId = GrantRoleId, MemberId = member.Id,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        await AcceptAs(member, Pick(promptId, optionId));
        await _context.SaveChangesAsync();
        await SetResponsesAs(member, Pick(promptId));
        await _context.SaveChangesAsync();

        Assert.That(_context.RoleMembers.Any(rm => rm.Id == "rm-manual"), Is.True,
            "the manually assigned RoleMember row must survive");
    }

    [Test]
    public async Task UpdateMyResponses_ReSelecting_GrantsAgain()
    {
        var (promptId, optionId, member) = await SeedAnswerableGuild();
        await AcceptAs(member, Pick(promptId, optionId));
        await _context.SaveChangesAsync();
        await SetResponsesAs(member, Pick(promptId));
        await _context.SaveChangesAsync();

        await SetResponsesAs(member, Pick(promptId, optionId));
        await _context.SaveChangesAsync();

        Assert.That(_context.RoleMembers.Any(rm => rm.MemberId == member.Id && rm.RoleId == GrantRoleId), Is.True);
    }

    [Test]
    public async Task UpdateMyResponses_Deselecting_KeepsRoleAnotherSelectedOptionAlsoGrants()
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets();

        // Two options in the same prompt handing out the same role.
        var dto = ConfigWithPrompt(channelIds: []);
        dto.Prompts[0].Options.Add(new OnboardingPromptOptionDto { Title = "Streaming", RoleIds = [GrantRoleId] });
        var saved = await PutConfig(dto);
        var promptId = saved.Prompts[0].Id!;
        var (first, second) = (saved.Prompts[0].Options[0].Id!, saved.Prompts[0].Options[1].Id!);

        var member = new GuildMember
        {
            Id = "member-joiner", GuildId = GuildId, UserId = "user-joiner", JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"user-joiner#{GuildId}", OnboardingCompletedAt = null,
        };
        _context.GuildMembers.Add(member);
        await _context.SaveChangesAsync();

        await AcceptAs(member, Pick(promptId, first, second));
        await _context.SaveChangesAsync();

        await SetResponsesAs(member, Pick(promptId, second));
        await _context.SaveChangesAsync();

        Assert.That(_context.RoleMembers.Any(rm => rm.MemberId == member.Id && rm.RoleId == GrantRoleId), Is.True,
            "the still-selected option keeps the role alive");
    }

    [Test]
    public async Task UpdateMyResponses_DroppingARequiredPrompt_ReturnsBadRequest()
    {
        var (promptId, optionId, member) = await SeedAnswerableGuild(required: true);
        await AcceptAs(member, Pick(promptId, optionId));
        await _context.SaveChangesAsync();

        var result = await SetResponsesAs(member, new OnboardingResponsesDto());

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateMyResponses_InvalidatesPermissionCacheAndNotifiesBots()
    {
        var (promptId, optionId, member) = await SeedAnswerableGuild();
        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, member.UserId);
        _cache.SetEntry(cacheKey, "stale-cached-permissions");
        _bus.Published.Clear();

        await SetResponsesAs(member, Pick(promptId, optionId));

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(cacheKey), Is.False);
            Assert.That(_bus.Published.OfType<MemberUpdatedForBots>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task GetMyStatus_ReturnsOnlyJoinFlowPrompts()
    {
        await SeedManagerMember(rolePosition: 10);
        await SeedGrantTargets();
        var dto = ConfigWithPrompt();
        dto.Prompts.Add(new OnboardingPromptDto
        {
            Title = "Pick your colour",
            InOnboarding = false,
            Options = [new OnboardingPromptOptionDto { Title = "Blue", RoleIds = [GrantRoleId] }],
        });
        await PutConfig(dto);

        var result = await _endpoint.GetMyStatus(GuildId, _context, _configService, TestPrincipal.Create(UserId));

        var value = ((IValueHttpResult)result).Value!;
        var prompts = (List<OnboardingPromptDto>)value.GetType().GetProperty("prompts")!.GetValue(value)!;
        Assert.That(prompts, Has.Count.EqualTo(1), "prompts flagged out of the join flow belong to Channels & Roles only");
    }
}
