using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Contracts.Dto.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers InviteEndpoint: listing/creating/fetching/revoking invites, and RedeemInviteAsync -
/// including the verification-level join-gating logic (guild.VerificationLevel.MeetsRequirement),
/// ban check, expiry/exhaustion, and the onboarding-pending join path.
/// </summary>
[TestFixture]
public class InviteEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";
    private const string EveryoneRoleId = "role-everyone";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private FakeInvokingMessageBus _bus = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private GuildInviteAudienceService _audience = null!;
    private VanityUrlService _vanity = null!;
    private FakeInvitePreviewRateLimiter _limiter = null!;
    private AuditLogService _auditLog = null!;
    private InviteEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _bus = new FakeInvokingMessageBus();
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _audience = new GuildInviteAudienceService(_context, _hydrateService);
        _vanity = new VanityUrlService(_context, NullLogger<VanityUrlService>.Instance);
        _limiter = new FakeInvitePreviewRateLimiter();
        _auditLog = new AuditLogService(_context);
        _endpoint = new InviteEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DefaultHttpContext Http() => new();

    private static Guild.Domain.Aggregates.Guild MakeGuild(GuildVerificationLevel level = GuildVerificationLevel.None) => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild", VerificationLevel = level,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ProfileDto MakeProfile(string userId) => new()
    {
        Id = userId, UserId = userId, UserName = "Tester", Hash = 1234, AvatarUrl = "", BannerUrl = "",
    };

    /// <summary>A member holding the two permissions the invite surface now asks for: ManageGuild to
    /// see and revoke the guild's invites, CreateInvite to mint one.</summary>
    private async Task SeedManagerMember()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageGuild | Permissions.CreateInvite, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    /// <summary>A member with no guild-wide grant at all, holding ManageChannel only through a
    /// channel overwrite - the second of the two grants that may revoke an invite.</summary>
    private async Task<Channel> SeedChannelModeratorAsync(string userId, string memberId)
    {
        var channel = new Channel
        {
            Id = "channel-1", GuildId = GuildId, Name = "general", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        _context.Channels.Add(channel);
        if (!await _context.Roles.AnyAsync(r => r.GuildId == GuildId && r.Type == RoleType.Everyone))
        {
            _context.Roles.Add(new Role { Id = EveryoneRoleId, GuildId = GuildId, Name = "Everyone", Type = RoleType.Everyone, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        }
        _context.GuildMembers.Add(new GuildMember { Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = userId.ToUpperInvariant() });
        _context.RoleMembers.Add(new RoleMember { Id = $"rm-everyone-{memberId}", RoleId = EveryoneRoleId, MemberId = memberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = "cperm-1", ChannelId = channel.Id, MemberId = memberId,
            AllowPermissions = Permissions.ManageChannel | Permissions.ViewChannel,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        // Cleared so the seeded channel and its overwrite are not sitting in the change tracker
        // when the endpoint maps its answer.
        _context.ChangeTracker.Clear();
        return channel;
    }

    private async Task<GuildInvite> SeedInvite(InviteType type = InviteType.Permanent, DateTimeOffset? expiresAt = null,
        int? maxUses = null, int useCount = 0, InviteState state = InviteState.Active, string? channelId = null,
        bool temporary = false)
    {
        var invite = GuildInvite.Create(new CreateGuildInviteParams
        {
            GuildId = GuildId, Type = type, ExpiresAt = expiresAt, MaxUses = maxUses,
            ChannelId = channelId, Temporary = temporary,
        });
        invite.UseCount = useCount;
        invite.State = state;
        _context.GuildInvites.Add(invite);
        await _context.SaveChangesAsync();
        return invite;
    }

    private Task<IResult> ListAsync(string guildId, string userId, bool includeRevoked = false) =>
        _endpoint.GetInvitesAsync(guildId, includeRevoked, _context, TestPrincipal.Create(userId), _permissionService);

    private Task<IResult> CreateAsync(string guildId, CreateInviteDto dto, System.Security.Claims.ClaimsPrincipal principal) =>
        _endpoint.CreateInviteAsync(guildId, dto, _context, principal, _permissionService, _auditLog, _hub, _audience, _bus);

    private Task<IResult> DeleteAsync(string inviteId, System.Security.Claims.ClaimsPrincipal principal) =>
        _endpoint.DeleteInviteAsync(inviteId, _context, principal, _permissionService, _auditLog, _hub, _audience, _bus);

    private Task<IResult> PreviewAsync(string inviteId) =>
        _endpoint.GetInviteAsync(inviteId, _context, Http(), _limiter, _vanity);

    private Task<IResult> RedeemAsync(string inviteId, System.Security.Claims.ClaimsPrincipal principal) =>
        _endpoint.RedeemInviteAsync(inviteId, principal, _context, _cache, _bus, _hub, _hydrateService, _vanity);

    // ══════════════════════════════════════════════════════════════════════ GetInvitesAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetInvites_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.GetInvitesAsync(GuildId, false, _context, TestPrincipal.CreateAnonymous(), _permissionService);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    /// <summary>The list used to be gated on ManageChannel.</summary>
    [Test]
    public async Task GetInvites_LacksManageGuild_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await ListAsync(GuildId, UserId);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetInvites_ManageChannelAlone_IsNoLongerEnough()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "chanmod", Permissions = Permissions.ManageChannel, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await ListAsync(GuildId, UserId);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetInvites_Valid_ReturnsInvites()
    {
        await SeedManagerMember();
        await SeedInvite();

        var result = await ListAsync(GuildId, UserId);
        var ok = result as Ok<List<InviteDto>>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(ok!.Value!.Count, Is.EqualTo(1));
    }

    /// <summary>The whole point of deriving state on read: nothing writes Expired when the clock
    /// passes, so a list that reported the stored column reported Active forever.</summary>
    [Test]
    public async Task GetInvites_PastExpiry_ReportsExpiredWithoutAnythingHavingWrittenIt()
    {
        await SeedManagerMember();
        var invite = await SeedInvite(expiresAt: DateTimeOffset.UtcNow.AddDays(-3));

        var ok = (Ok<List<InviteDto>>)await ListAsync(GuildId, UserId);

        Assert.That(ok.Value!.Single().State, Is.EqualTo(InviteState.Expired));

        var stored = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.That(stored.State, Is.EqualTo(InviteState.Active),
            "derived on read means the row is untouched - no sweeper, no second writer");
    }

    [Test]
    public async Task GetInvites_Exhausted_ReportsExpired()
    {
        await SeedManagerMember();
        await SeedInvite(maxUses: 2, useCount: 2);

        var ok = (Ok<List<InviteDto>>)await ListAsync(GuildId, UserId);
        Assert.That(ok.Value!.Single().State, Is.EqualTo(InviteState.Expired));
    }

    /// <summary>Revoked rows survive now.</summary>
    [Test]
    public async Task GetInvites_RevokedAreHiddenByDefaultAndVisibleOnRequest()
    {
        await SeedManagerMember();
        var invite = await SeedInvite();
        await DeleteAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var hidden = (Ok<List<InviteDto>>)await ListAsync(GuildId, UserId);
        var shown = (Ok<List<InviteDto>>)await ListAsync(GuildId, UserId, includeRevoked: true);

        Assert.Multiple(() =>
        {
            Assert.That(hidden.Value, Is.Empty);
            Assert.That(shown.Value!.Single().State, Is.EqualTo(InviteState.Revoked));
        });
    }

    // ══════════════════════════════════════════════════════════════════════ CreateInviteAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateInvite_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await CreateAsync(GuildId, new CreateInviteDto(), TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateInvite_LacksCreateInvitePermission_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await CreateAsync(GuildId, new CreateInviteDto(), TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateInvite_GuildDoesNotExist_ReturnsForbidBeforeAnythingElse()
    {
        // Permission resolution against a nonexistent guild resolves to nothing, so the guild lookup
        // is never reached - which is the real behaviour and the one worth pinning.
        var result = await CreateAsync("nonexistent", new CreateInviteDto(), TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateInvite_Valid_PersistsInviteWithGeneratedCode()
    {
        await SeedManagerMember();

        var result = await CreateAsync(GuildId, new CreateInviteDto { Type = InviteType.OneTime, MaxUses = 5 }, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<InviteDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(ok!.Value!.Code, Is.Not.Null.And.Not.Empty);
        Assert.That(await _context.GuildInvites.AsNoTracking().AnyAsync(i => i.Id == ok.Value.Id && i.MaxUses == 5), Is.True);
    }

    /// <summary>MaxUses reaching the wire is the point of the DTO change - the column has always
    /// existed and neither client could set it.</summary>
    [Test]
    public async Task CreateInvite_MaxUsesRoundTripsToTheDto()
    {
        await SeedManagerMember();

        var ok = (Ok<InviteDto>)await CreateAsync(GuildId, new CreateInviteDto { MaxUses = 3 }, TestPrincipal.Create(UserId));

        Assert.That(ok.Value!.MaxUses, Is.EqualTo(3));
    }

    [Test]
    public async Task CreateInvite_ZeroMaxUses_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var result = await CreateAsync(GuildId, new CreateInviteDto { MaxUses = 0 }, TestPrincipal.Create(UserId));

        // Zero would mint an invite that is exhausted the moment it exists, which is a link somebody
        // is about to share. Unlimited is expressed by omitting the field, not by zero.
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateInvite_RecordsTheInviter()
    {
        await SeedManagerMember();

        var ok = (Ok<InviteDto>)await CreateAsync(GuildId, new CreateInviteDto(), TestPrincipal.Create(UserId));

        Assert.That(ok.Value!.InviterId, Is.EqualTo(UserId),
            "this is what makes GuildMember.InviteCode's 'who brought this member in' answerable");
    }

    [Test]
    public async Task CreateInvite_TemporaryFlagRoundTrips()
    {
        await SeedManagerMember();

        var ok = (Ok<InviteDto>)await CreateAsync(GuildId, new CreateInviteDto { Temporary = true }, TestPrincipal.Create(UserId));

        Assert.That(ok.Value!.Temporary, Is.True);
    }

    [Test]
    public async Task CreateInvite_VoiceTargetWithoutAChannel_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var result = await CreateAsync(GuildId, new CreateInviteDto { TargetType = InviteTargetType.VoiceChannel }, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateInvite_VoiceTargetPointingAtATextChannel_ReturnsBadRequest()
    {
        await SeedManagerMember();
        _context.Channels.Add(new Channel { Id = "text-1", GuildId = GuildId, Name = "general", Type = ChannelType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await CreateAsync(GuildId, new CreateInviteDto { TargetType = InviteTargetType.VoiceChannel, ChannelId = "text-1" }, TestPrincipal.Create(UserId));

        // Refused at creation, not at redemption: the person who can still fix a bad target is the
        // one standing here, not the stranger who opens the link a week later.
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateInvite_VoiceTargetInAnotherGuild_ReturnsBadRequest()
    {
        await SeedManagerMember();
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild { Id = "guild-2", OwnerId = OwnerId, Name = "Other", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.Channels.Add(new Channel { Id = "voice-elsewhere", GuildId = "guild-2", Name = "vc", Type = ChannelType.Voice, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await CreateAsync(GuildId, new CreateInviteDto { TargetType = InviteTargetType.VoiceChannel, ChannelId = "voice-elsewhere" }, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateInvite_ValidVoiceTarget_IsAccepted()
    {
        await SeedManagerMember();
        _context.Channels.Add(new Channel { Id = "voice-1", GuildId = GuildId, Name = "Lounge", Type = ChannelType.Voice, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var ok = (Ok<InviteDto>)await CreateAsync(GuildId,
            new CreateInviteDto { TargetType = InviteTargetType.VoiceChannel, ChannelId = "voice-1" }, TestPrincipal.Create(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(ok.Value!.TargetType, Is.EqualTo(InviteTargetType.VoiceChannel));
            Assert.That(ok.Value.ChannelId, Is.EqualTo("voice-1"));
        });
    }

    [Test]
    public async Task CreateInvite_PublishesInviteCreatedForBots()
    {
        await SeedManagerMember();

        await CreateAsync(GuildId, new CreateInviteDto(), TestPrincipal.Create(UserId));

        var published = _bus.Published.OfType<Guild.Contracts.Bus.Events.InviteCreatedForBots>().SingleOrDefault();
        Assert.That(published, Is.Not.Null);
        Assert.That(published!.GuildId, Is.EqualTo(GuildId));
        Assert.That(published.Code, Is.Not.Null.And.Not.Empty);
        Assert.That(published.InviterId, Is.EqualTo(UserId));
    }

    /// <summary>The code is the credential, so the broadcast goes to the ManageGuild holders rather
    /// than to everybody online.</summary>
    [Test]
    public async Task CreateInvite_BroadcastsOnlyToManageGuildHolders()
    {
        await SeedManagerMember();
        _context.GuildMembers.Add(new GuildMember { Id = "member-2", GuildId = GuildId, UserId = "user-2", JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-2" });
        await _context.SaveChangesAsync();

        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(
                new MemberPresenceState { MemberId = MemberId, UserId = UserId, Status = "Online" },
                new MemberPresenceState { MemberId = "member-2", UserId = "user-2", Status = "Online" }),
            NullLogger<GuildHydrateService>.Instance);

        var audience = new GuildInviteAudienceService(_context, hydrate);
        await _endpoint.CreateInviteAsync(GuildId, new CreateInviteDto(), _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, audience, _bus);

        var recipients = ((FakeHubClients)_hub.Clients).RecipientsOf("guild.InviteCreated");
        Assert.Multiple(() =>
        {
            Assert.That(recipients, Does.Contain(UserId));
            Assert.That(recipients, Does.Not.Contain("user-2"),
                "an ordinary member must not be handed a live join code by a realtime event");
        });
    }

    [Test]
    public async Task CreateInvite_NobodyOnline_SendsNothingAndStillPublishesForBots()
    {
        await SeedManagerMember();

        await CreateAsync(GuildId, new CreateInviteDto(), TestPrincipal.Create(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(((FakeHubClients)_hub.Clients).SentMessages, Is.Empty);
            Assert.That(_bus.Published.OfType<Guild.Contracts.Bus.Events.InviteCreatedForBots>().Any(), Is.True,
                "the bot gateway is not a presence-gated audience");
        });
    }

    // ══════════════════════════════════════════════════════════════════════ GetInviteAsync /
    // GetInviteByCodeAsync ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetInvite_DoesNotExist_ReturnsNotFound()
    {
        var result = await PreviewAsync("nonexistent");
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetInvite_ById_ReturnsInvite()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();

        var result = await PreviewAsync(invite.Id);
        var ok = result as Ok<InviteDto>;
        Assert.That(ok!.Value!.Id, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task GetInvite_ByCodeFallback_ReturnsInvite()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();

        var result = await PreviewAsync(invite.Code);
        var ok = result as Ok<InviteDto>;
        Assert.That(ok!.Value!.Id, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task GetInvite_PastExpiry_ReportsExpired()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite(expiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        var ok = (Ok<InviteDto>)await PreviewAsync(invite.Id);

        // Both clients had grown a local re-derivation to paper over this returning Active.
        Assert.That(ok.Value!.State, Is.EqualTo(InviteState.Expired));
    }

    [Test]
    public async Task GetInvite_Revoked_ReturnsNotFound()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite(state: InviteState.Revoked);

        // The row survives now, but a revoked code has to look exactly as gone as a deleted one did.
        Assert.That(await PreviewAsync(invite.Id), Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetInvite_RateLimited_Returns429WithoutTouchingTheDatabase()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();

        var limited = new FakeInvitePreviewRateLimiter(allow: false);
        var result = await _endpoint.GetInviteAsync(invite.Id, _context, Http(), limited, _vanity);

        Assert.That(result.GetType().Name, Does.StartWith("JsonHttpResult"));
        var statusCode = (int?)result.GetType().GetProperty("StatusCode")!.GetValue(result);
        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status429TooManyRequests));
    }

    [Test]
    public async Task GetInvite_SpendsExactlyOneTokenPerRequest()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();

        await PreviewAsync(invite.Id);
        await PreviewAsync("nonexistent");

        Assert.That(_limiter.Calls, Is.EqualTo(2),
            "a miss is the request worth pricing - it is the one that probes the code space");
    }

    [Test]
    public async Task GetInviteByCode_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.GetInviteByCodeAsync("NOTREAL1", _context, Http(), _limiter);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetInviteByCode_RateLimited_Returns429()
    {
        var limited = new FakeInvitePreviewRateLimiter(allow: false);
        var result = await _endpoint.GetInviteByCodeAsync("NOTREAL1", _context, Http(), limited);

        var statusCode = (int?)result.GetType().GetProperty("StatusCode")!.GetValue(result);
        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status429TooManyRequests));
    }

    // ══════════════════════════════════════════════════════════════════════ Vanity resolution
    // ══════════════════════════════════════════════════════════════════════

    private async Task<GuildInvite> SeedVanityAsync(string slug)
    {
        var guild = MakeGuild();
        _context.Guilds.Add(guild);
        await _context.SaveChangesAsync();

        var invite = await SeedInvite();
        guild.VanityUrl = slug;
        guild.VanityUrlSetAt = DateTimeOffset.UtcNow;
        guild.VanityInviteId = invite.Id;
        await _context.SaveChangesAsync();
        return invite;
    }

    [Test]
    public async Task GetInviteByVanity_ResolvesToTheSameInviteACodeDoes()
    {
        var invite = await SeedVanityAsync("the-flat");

        var ok = (Ok<InviteDto>)await _endpoint.GetInviteByVanityAsync("the-flat", _context, Http(), _limiter, _vanity);

        Assert.That(ok.Value!.Id, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task GetInviteByVanity_IsCaseInsensitive()
    {
        var invite = await SeedVanityAsync("the-flat");

        var ok = (Ok<InviteDto>)await _endpoint.GetInviteByVanityAsync("  The-FLAT ", _context, Http(), _limiter, _vanity);

        Assert.That(ok.Value!.Id, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task GetInvite_CatchAllRoute_FallsThroughToVanity()
    {
        var invite = await SeedVanityAsync("the-flat");

        var ok = (Ok<InviteDto>)await PreviewAsync("the-flat");

        Assert.That(ok.Value!.Id, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task GetInviteByVanity_UnknownSlug_ReturnsNotFound()
    {
        await SeedVanityAsync("the-flat");

        var result = await _endpoint.GetInviteByVanityAsync("some-other-house", _context, Http(), _limiter, _vanity);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetInviteByVanity_MalformedSlug_ReturnsNotFoundWithoutQuerying()
    {
        var result = await _endpoint.GetInviteByVanityAsync("!!", _context, Http(), _limiter, _vanity);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetInviteByVanity_BackingInviteRevoked_ReturnsNotFound()
    {
        var invite = await SeedVanityAsync("the-flat");
        invite.Revoke(DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetInviteByVanityAsync("the-flat", _context, Http(), _limiter, _vanity);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════ DeleteInviteAsync
    // (revocation) ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteInvite_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await DeleteAsync("nonexistent", TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task DeleteInvite_DoesNotExist_ReturnsNotFound()
    {
        var result = await DeleteAsync("nonexistent", TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteInvite_LacksEitherGrant_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();

        var result = await DeleteAsync(invite.Id, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    /// <summary>The second of Discord's two grants: MANAGE_CHANNELS on the channel this invite lands
    /// on, held here only through a per-member channel overwrite.</summary>
    [Test]
    public async Task DeleteInvite_ManageChannelOnTheInvitesOwnChannel_IsEnough()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var channel = await SeedChannelModeratorAsync("chanmod", "member-chanmod");
        var invite = await SeedInvite(channelId: channel.Id);

        var result = await DeleteAsync(invite.Id, TestPrincipal.Create("chanmod"));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<InviteDto>>());
        var stored = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.That(stored.State, Is.EqualTo(InviteState.Revoked));
    }

    /// <summary>And only on its own channel - the same moderator may not reach an invite pointing
    /// somewhere else, or at the guild as a whole.</summary>
    [Test]
    public async Task DeleteInvite_ManageChannelElsewhere_IsNotEnough()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        await SeedChannelModeratorAsync("chanmod", "member-chanmod");
        var invite = await SeedInvite();

        var result = await DeleteAsync(invite.Id, TestPrincipal.Create("chanmod"));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteInvite_Valid_RevokesWithoutDeletingTheRow()
    {
        await SeedManagerMember();
        var invite = await SeedInvite();

        var result = await DeleteAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<InviteDto>>());

        var stored = await _context.GuildInvites.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invite.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.Not.Null, "the row is what GuildMember.InviteId points at");
            Assert.That(stored!.State, Is.EqualTo(InviteState.Revoked));
            Assert.That(stored.RevokedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task DeleteInvite_ResponseStillCarriesTheInvite()
    {
        await SeedManagerMember();
        var invite = await SeedInvite();

        var ok = (Ok<InviteDto>)await DeleteAsync(invite.Id, TestPrincipal.Create(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(ok.Value!.Id, Is.EqualTo(invite.Id));
            Assert.That(ok.Value.State, Is.EqualTo(InviteState.Revoked));
        });
    }

    [Test]
    public async Task DeleteInvite_Twice_IsIdempotentAndLogsOnce()
    {
        await SeedManagerMember();
        var invite = await SeedInvite();

        await DeleteAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();
        var second = await DeleteAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(second, Is.InstanceOf<Ok<InviteDto>>());
        Assert.That(await _context.Set<GuildAuditLogEntry>().AsNoTracking()
            .CountAsync(e => e.ActionType == AuditActionType.InviteDeleted && e.TargetId == invite.Id), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteInvite_PublishesInviteDeletedForBots()
    {
        await SeedManagerMember();
        var invite = await SeedInvite();

        await DeleteAsync(invite.Id, TestPrincipal.Create(UserId));

        var published = _bus.Published.OfType<Guild.Contracts.Bus.Events.InviteDeletedForBots>().SingleOrDefault();
        Assert.That(published, Is.Not.Null);
        Assert.That(published!.Code, Is.EqualTo(invite.Code));
    }

    [Test]
    public async Task DeleteInvite_BroadcastsOnlyToManageGuildHolders()
    {
        await SeedManagerMember();
        _context.GuildMembers.Add(new GuildMember { Id = "member-2", GuildId = GuildId, UserId = "user-2", JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-2" });
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();

        var hydrate = new GuildHydrateService(
            RedisTestFactory.CreateWithPresence(
                new MemberPresenceState { MemberId = MemberId, UserId = UserId, Status = "Online" },
                new MemberPresenceState { MemberId = "member-2", UserId = "user-2", Status = "Online" }),
            NullLogger<GuildHydrateService>.Instance);

        await _endpoint.DeleteInviteAsync(invite.Id, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog,
            _hub, new GuildInviteAudienceService(_context, hydrate), _bus);

        var recipients = ((FakeHubClients)_hub.Clients).RecipientsOf("guild.InviteDeleted");
        Assert.Multiple(() =>
        {
            Assert.That(recipients, Does.Contain(UserId));
            Assert.That(recipients, Does.Not.Contain("user-2"));
        });
    }

    [Test]
    public async Task DeleteInvite_WritesAuditLogEntry()
    {
        await SeedManagerMember();
        var invite = await SeedInvite();

        await DeleteAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var entry = await _context.Set<GuildAuditLogEntry>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.GuildId == GuildId && e.ActionType == AuditActionType.InviteDeleted);
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.TargetId, Is.EqualTo(invite.Id));
    }

    [Test]
    public async Task CreateInvite_WritesAuditLogEntry()
    {
        await SeedManagerMember();

        await CreateAsync(GuildId, new CreateInviteDto(), TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(await _context.Set<GuildAuditLogEntry>().AsNoTracking()
            .AnyAsync(e => e.GuildId == GuildId && e.ActionType == AuditActionType.InviteCreated), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ RedeemInviteAsync
    // ══════════════════════════════════════════════════════════════════════

    private async Task<Guild.Domain.Aggregates.Guild> SeedRedeemableGuild(GuildVerificationLevel level = GuildVerificationLevel.None)
    {
        var guild = MakeGuild(level);
        _context.Guilds.Add(guild);
        var everyone = new Role { Id = EveryoneRoleId, GuildId = GuildId, Name = "Everyone", Type = RoleType.Everyone, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _context.Roles.Add(everyone);
        await _context.SaveChangesAsync();
        return guild;
    }

    [Test]
    public async Task RedeemInvite_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await RedeemAsync("nonexistent", TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task RedeemInvite_ProfileNotFound_ReturnsBadRequest()
    {
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = null });

        var result = await RedeemAsync("nonexistent", TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task RedeemInvite_DoesNotExist_ReturnsNotFound()
    {
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync("nonexistent", TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task RedeemInvite_ExplicitlyExpiredState_ReturnsBadRequest()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(state: InviteState.Expired);
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    /// <summary>Revocation answers 404 rather than 400: to whoever is holding the code, it has to be
    /// indistinguishable from a code that was never real.</summary>
    [Test]
    public async Task RedeemInvite_Revoked_ReturnsNotFound()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(state: InviteState.Revoked);
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task RedeemInvite_PastExpiresAt_ReturnsBadRequest()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task RedeemInvite_Exhausted_ReturnsBadRequest()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(maxUses: 1, useCount: 1);
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task RedeemInvite_UserIsBanned_ReturnsForbid()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _context.Set<GuildBan>().Add(GuildBan.Create(new CreateGuildBanParams { GuildId = GuildId, BannedUserId = UserId, BannedByUserId = OwnerId }));
        await _context.SaveChangesAsync();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task RedeemInvite_VerificationLevelNotMet_ReturnsForbidJson()
    {
        await SeedRedeemableGuild(GuildVerificationLevel.Low);
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });
        _bus.SetResponse<GetUserByIdRequest>(new GetUserByIdResponse
        {
            User = new ApplicationUserDto { Id = UserId, Email = "a@b.com", EmailConfirmed = false, CreatedAt = DateTimeOffset.UtcNow },
        });

        var result = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));

        // Results.Json(...) returns JsonHttpResult<T> where T is inferred from the anonymous
        // object literal, so the concrete generic type isn't known here - assert via reflection.
        Assert.That(result.GetType().Name, Does.StartWith("JsonHttpResult"));
        var statusCode = (int?)result.GetType().GetProperty("StatusCode")!.GetValue(result);
        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public async Task RedeemInvite_Valid_CreatesMemberAndAssignsEveryoneRole()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Accepted<RedeemInviteResultDto>>());
        var member = await _context.GuildMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GuildId == GuildId && m.UserId == UserId);
        Assert.That(member, Is.Not.Null);
        Assert.That(member!.OnboardingCompletedAt, Is.Not.Null, "no onboarding configured, so join auto-completes it");
        Assert.That(await _context.RoleMembers.AsNoTracking().AnyAsync(rm => rm.RoleId == EveryoneRoleId && rm.MemberId == member.Id), Is.True);
    }

    [Test]
    public async Task RedeemInvite_Valid_IncrementsUseCount()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var reloaded = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.That(reloaded.UseCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RedeemInvite_OneTimeType_MarksInviteExpiredAfterUse()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(type: InviteType.OneTime);
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var reloaded = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.That(reloaded.State, Is.EqualTo(InviteState.Expired));
    }

    /// <summary>The persistent half of "join my call": the redeem answer has to carry enough to land
    /// the joiner in the channel by itself, because the link may be opened long after anybody was
    /// online to ring them.</summary>
    [Test]
    public async Task RedeemInvite_VoiceTarget_TellsTheClientToConnect()
    {
        var guild = await SeedRedeemableGuild();
        _context.Channels.Add(new Channel { Id = "voice-1", GuildId = GuildId, Name = "Lounge", Type = ChannelType.Voice, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var invite = GuildInvite.Create(new CreateGuildInviteParams
        {
            GuildId = guild.Id, Type = InviteType.Permanent, ChannelId = "voice-1",
            TargetType = InviteTargetType.VoiceChannel, TargetUserId = "streamer-1",
        });
        _context.GuildInvites.Add(invite);
        await _context.SaveChangesAsync();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var accepted = (Accepted<RedeemInviteResultDto>)await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Value!.JoinVoice, Is.True);
            Assert.That(accepted.Value.ChannelId, Is.EqualTo("voice-1"));
            Assert.That(accepted.Value.TargetType, Is.EqualTo(InviteTargetType.VoiceChannel));
            Assert.That(accepted.Value.TargetUserId, Is.EqualTo("streamer-1"));
        });
    }

    /// <summary>A voice target only survives as far as its channel does.</summary>
    [Test]
    public async Task RedeemInvite_VoiceTargetChannelGone_StillJoinsButDoesNotAskForVoice()
    {
        var guild = await SeedRedeemableGuild();
        var invite = GuildInvite.Create(new CreateGuildInviteParams
        {
            GuildId = guild.Id, Type = InviteType.Permanent, ChannelId = "voice-deleted",
            TargetType = InviteTargetType.VoiceChannel,
        });
        _context.GuildInvites.Add(invite);
        await _context.SaveChangesAsync();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var accepted = (Accepted<RedeemInviteResultDto>)await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Value!.JoinVoice, Is.False);
            Assert.That(_context.GuildMembers.Any(m => m.GuildId == GuildId && m.UserId == UserId), Is.True);
        });
    }

    [Test]
    public async Task RedeemInvite_OrdinaryInvite_ReportsNoVoiceTarget()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var accepted = (Accepted<RedeemInviteResultDto>)await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Value!.TargetType, Is.EqualTo(InviteTargetType.None));
            Assert.That(accepted.Value.JoinVoice, Is.False);
            Assert.That(accepted.Value.TemporaryMembership, Is.False);
        });
    }

    [Test]
    public async Task RedeemInvite_TemporaryInvite_SnapshotsTheTermsOntoTheMember()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite(temporary: true);
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var accepted = (Accepted<RedeemInviteResultDto>)await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var member = await _context.GuildMembers.AsNoTracking().FirstAsync(m => m.UserId == UserId);
        Assert.Multiple(() =>
        {
            Assert.That(member.TemporaryMembership, Is.True);
            Assert.That(member.TemporaryEvictionDueAt, Is.Null, "joining is not a disconnect");
            Assert.That(accepted.Value!.TemporaryMembership, Is.True, "a client that does not say so leaves the member to discover it by being gone");
        });
    }

    [Test]
    public async Task RedeemInvite_ByVanitySlug_Joins()
    {
        var guild = MakeGuild();
        _context.Guilds.Add(guild);
        _context.Roles.Add(new Role { Id = EveryoneRoleId, GuildId = GuildId, Name = "Everyone", Type = RoleType.Everyone, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var invite = await SeedInvite();
        guild.VanityUrl = "the-flat";
        guild.VanityInviteId = invite.Id;
        await _context.SaveChangesAsync();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync("the-flat", TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Accepted<RedeemInviteResultDto>>());
        var reloaded = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.That(reloaded.UseCount, Is.EqualTo(1), "a vanity redeem is an ordinary redeem, counter and all");
    }

    /// <summary>
    /// The join must be committed by the time the endpoint returns, not left to the transactional
    /// middleware at the end of the handler.
    /// </summary>
    [Test]
    public async Task RedeemInvite_Valid_PersistsTheJoinBeforeAnythingCanResolvePermissions()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Accepted<RedeemInviteResultDto>>());
        var member = await _context.GuildMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GuildId == GuildId && m.UserId == UserId);
        Assert.That(member, Is.Not.Null, "the membership must be readable the moment redeem returns");
        var roleMember = await _context.RoleMembers.AsNoTracking()
            .FirstOrDefaultAsync(rm => rm.MemberId == member!.Id && rm.RoleId == EveryoneRoleId);
        Assert.That(roleMember, Is.Not.Null, "@everyone is where the joiner's base permissions come from");
    }

    /// <summary>A refused join must still commit nothing - the early returns above the insert are
    /// what keep a rejected redeem from leaving a membership behind, and moving the commit into the
    /// handler is exactly the change that could break that.</summary>
    [Test]
    public async Task RedeemInvite_MissingEveryoneRole_CommitsNoMembership()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));

        var members = await _context.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == GuildId && m.UserId == UserId).ToListAsync();
        Assert.That(members, Is.Empty);
    }

    [Test]
    public async Task RedeemInvite_Valid_InvalidatesUserPermissionCache()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });
        var cacheKey = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(cacheKey, "stale");

        await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));

        Assert.That(_cache.HasEntry(cacheKey), Is.False);
    }

    [Test]
    public async Task RedeemInvite_Valid_PublishesMemberJoinedForBots()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));

        Assert.That(_bus.Published.OfType<Guild.Contracts.Bus.Events.MemberJoinedForBots>().Any(e => e.UserId == UserId && e.GuildId == GuildId), Is.True);
    }

    [Test]
    public async Task RedeemInvite_Valid_SnapshotsInviteCodeOnMember()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var member = await _context.GuildMembers.AsNoTracking().FirstAsync(m => m.GuildId == GuildId && m.UserId == UserId);
        Assert.That(member.InviteId, Is.EqualTo(invite.Id));
        Assert.That(member.InviteCode, Is.EqualTo(invite.Code), "attribution must survive the invite being revoked");
    }

    [Test]
    public async Task RedeemInvite_AlreadyAMember_ReturnsConflictAndDoesNotDuplicateOrBurnAUse()
    {
        await SeedRedeemableGuild();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var second = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(second, Is.InstanceOf<Conflict<string>>());
        Assert.That(await _context.GuildMembers.AsNoTracking().CountAsync(m => m.GuildId == GuildId && m.UserId == UserId), Is.EqualTo(1));

        var reloaded = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.That(reloaded.UseCount, Is.EqualTo(1), "a rejected re-redeem must not consume a use");
    }

    [Test]
    public async Task RedeemInvite_GuildHasNoEveryoneRole_ReturnsProblemInsteadOfThrowing()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();
        var invite = await SeedInvite();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await RedeemAsync(invite.Id, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ProblemHttpResult>());
    }

    // ══════════════════════════════════════════════════════════════════════
    // Invite revocation must not take the members with it
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The FK used to be ON DELETE CASCADE, so deleting an invite deleted every member who
    /// had joined through it - silently, since DeleteInviteAsync never loads the Members collection
    /// and so fires none of the kick/leave side effects. Asserted on the model rather than only
    /// behaviourally because the InMemory provider has no real FK to enforce. Still asserted even
    /// though the route no longer deletes: nothing else stops a future delete path reintroducing it.</summary>
    [Test]
    public void GuildMemberInviteForeignKey_IsSetNull_NotCascade()
    {
        var fk = _context.Model
            .FindEntityType(typeof(GuildMember))!
            .FindNavigation(nameof(GuildMember.Invite))!
            .ForeignKey;

        Assert.That(fk.DeleteBehavior, Is.EqualTo(DeleteBehavior.SetNull));
    }

    /// <summary>Revocation keeps the whole attribution chain, not just the code snapshot: the member
    /// still points at a row that still knows who created the invite.</summary>
    [Test]
    public async Task DeleteInvite_KeepsMembersAndTheLinkBackToTheInviter()
    {
        await SeedManagerMember();
        var invite = await SeedInvite();
        invite.InviterId = "inviter-1";
        await _context.SaveChangesAsync();

        var joiner = new GuildMember
        {
            Id = "member-joined", GuildId = GuildId, UserId = "user-joined", JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = "USER-JOINED", InviteId = invite.Id, InviteCode = invite.Code,
        };
        _context.GuildMembers.Add(joiner);
        await _context.SaveChangesAsync();

        await _context.GuildInvites.Include(i => i.Members).FirstAsync(i => i.Id == invite.Id);

        var result = await DeleteAsync(invite.Id, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<InviteDto>>());
        var survivor = await _context.GuildMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == "member-joined");
        Assert.Multiple(() =>
        {
            Assert.That(survivor, Is.Not.Null, "revoking an invite must not delete the members who used it");
            Assert.That(survivor!.InviteId, Is.EqualTo(invite.Id), "and the FK survives now too, because the row does");
            Assert.That(survivor.InviteCode, Is.EqualTo(invite.Code));
        });

        var stored = await _context.GuildInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.That(stored.InviterId, Is.EqualTo("inviter-1"),
            "'who brought this member in' is reachable through the surviving row");
    }
}
