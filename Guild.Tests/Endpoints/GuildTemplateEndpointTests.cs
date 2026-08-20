using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Endpoints.Guild;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers GuildTemplateEndpoint: snapshotting a guild's structure into a template
/// (CreateFromGuild), fetching it back (GetTemplate), and replaying it into a brand new guild
/// (CreateGuildFromTemplate) - including the TemplateSnapshot owned-JSON round trip through the
/// EF Core InMemory provider (Roles / UncategorizedChannels / Categories.Channels nesting).
/// </summary>
[TestFixture]
public class GuildTemplateEndpointTests
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
    private FakeInvokingMessageBus _bus = null!;
    private GuildTemplateEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = PermissionTestFactory.Create(_cache, _context);
        _auditLog = new AuditLogService(_context);
        _bus = new FakeInvokingMessageBus();
        _endpoint = new GuildTemplateEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Reads a property off the anonymous types these endpoints return via Results.Ok(new
    /// { ... }) - avoids adding a `dynamic`/Microsoft.CSharp dependency just for tests.</summary>
    private static T GetProp<T>(object obj, string name) => (T)obj.GetType().GetProperty(name)!.GetValue(obj)!;

    private static Guild.Domain.Aggregates.Guild MakeGuild(string ownerId = OwnerId, string id = GuildId) => new()
    {
        Id = id, OwnerId = ownerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedManagerMember()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "manager", Type = RoleType.None, Permissions = Permissions.ManageGuild,
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
        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════ CreateFromGuild
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateFromGuild_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateFromGuild(GuildId, new CreateGuildTemplateFromGuildDto { Name = "t" },
            _permissionService, _context, _auditLog, TestPrincipal.CreateAnonymous());

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateFromGuild_LacksManageGuild_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateFromGuild(GuildId, new CreateGuildTemplateFromGuildDto { Name = "t" },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateFromGuild_GuildDoesNotExist_ReturnsForbid()
    {
        // The permission check itself resolves to false for a nonexistent guild (no owner, no
        // roles to match), so this short-circuits to Forbid before the explicit NotFound check
        // for the guild row is ever reached.
        var result = await _endpoint.CreateFromGuild("nonexistent", new CreateGuildTemplateFromGuildDto { Name = "t" },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateFromGuild_Valid_SnapshotsStructureAndRoundTripsThroughJson()
    {
        await SeedManagerMember();

        // An Everyone role (as every real guild has, via Guild.Create) plus a second custom role -
        // both non-Everyone roles should appear in the snapshot, with Everyone re-inserted at 0.
        var everyoneRole = Role.CreateEveryoneRole(GuildId, MemberId);
        _context.Roles.Add(everyoneRole);
        var customRole = Role.Create(new CreateRoleParams { Name = "vip", GuildId = GuildId, Color = "#abcdef", Permissions = Permissions.ManageEvents });
        customRole.Position = 5;
        _context.Roles.Add(customRole);

        var category = Category.Create(new CreateCategoryParams { Name = "Text Channels", GuildId = GuildId, Position = 0 });
        _context.Categories.Add(category);

        var textChannel = Channel.Create(new CreateChannelParams { Name = "general", Type = ChannelType.Text, GuildId = GuildId, CategoryId = category.Id, Description = "d", Position = 0 });
        var ticketChannel = Channel.Create(new CreateChannelParams { Name = "ticket-1", Type = ChannelType.Ticket, GuildId = GuildId, CategoryId = category.Id, Description = "d", Position = 1 });
        _context.Channels.AddRange(textChannel, ticketChannel);

        var uncategorizedVoice = Channel.Create(new CreateChannelParams { Name = "afk", Type = ChannelType.Voice, GuildId = GuildId, Description = "d", Position = 0 });
        _context.Channels.Add(uncategorizedVoice);
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateFromGuild(GuildId, new CreateGuildTemplateFromGuildDto { Name = "My Template", Description = "desc" },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        var templateId = GetProp<string>(((IValueHttpResult)result).Value!, "Id");
        var reloaded = await _context.GuildTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId);

        Assert.That(reloaded, Is.Not.Null, "Template must be persisted (this endpoint commits manually)");
        var snapshot = reloaded!.Snapshot;

        Assert.Multiple(() =>
        {
            // Everyone role inserted alongside the "manager" role and "vip" role = 3 total.
            Assert.That(snapshot.Roles, Has.Count.EqualTo(3));
            Assert.That(snapshot.Roles.Any(r => r.Name == "Everyone"), Is.True);
            Assert.That(snapshot.Roles.Any(r => r.Name == "vip" && r.Permissions == Permissions.ManageEvents), Is.True);
            Assert.That(snapshot.Roles.Any(r => r.Name == "manager"), Is.True);

            // Ticket channels are excluded from the snapshot entirely.
            Assert.That(snapshot.Categories, Has.Count.EqualTo(1));
            Assert.That(snapshot.Categories[0].Channels, Has.Count.EqualTo(1));
            Assert.That(snapshot.Categories[0].Channels[0].Name, Is.EqualTo("general"));

            Assert.That(snapshot.UncategorizedChannels, Has.Count.EqualTo(1));
            Assert.That(snapshot.UncategorizedChannels[0].Name, Is.EqualTo("afk"));
            Assert.That(snapshot.UncategorizedChannels[0].Type, Is.EqualTo(ChannelType.Voice));
        });
    }

    [Test]
    public async Task CreateFromGuild_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();

        await _endpoint.CreateFromGuild(GuildId, new CreateGuildTemplateFromGuildDto { Name = "t" },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.GuildId == GuildId).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].ActionType, Is.EqualTo(AuditActionType.TemplateCreated));
    }

    // ══════════════════════════════════════════════════════════════════════ GetTemplate
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetTemplate_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.GetTemplate("nonexistent", _context);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetTemplate_Exists_ReturnsSnapshot()
    {
        var template = GuildTemplate.Create(new CreateGuildTemplateParams
        {
            Name = "My Template",
            CreatorUserId = UserId,
            SourceGuildId = GuildId,
            Snapshot = new TemplateSnapshot
            {
                Roles = [new TemplateRole { Name = "Everyone", Position = 0 }],
            },
        });
        _context.GuildTemplates.Add(template);
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetTemplate(template.Id, _context);

        var value = ((IValueHttpResult)result).Value!;
        Assert.That(GetProp<string>(value, "Name"), Is.EqualTo("My Template"));
    }

    // ══════════════════════════════════════════════════════════════════════
    // CreateGuildFromTemplate
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateGuildFromTemplate_IsEveryoneFlagWins_OverACustomRoleNamedEveryone()
    {
        // The old identification was Name == "Everyone" && Position == 0, which a custom role can
        // shadow - the snapshot below has one deliberately sitting in that slot. The flag decides.
        var template = GuildTemplate.Create(new CreateGuildTemplateParams
        {
            Name = "Shadowed", CreatorUserId = OwnerId,
            Snapshot = new TemplateSnapshot
            {
                Roles =
                [
                    new TemplateRole { Name = "Everyone", Position = 0, Permissions = Permissions.ManageGuild },
                    new TemplateRole { Name = "Members", Position = 1, Permissions = Permissions.SendMessages, IsEveryone = true },
                ],
            },
        });
        _context.GuildTemplates.Add(template);
        await _context.SaveChangesAsync();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.CreateGuildFromTemplate(template.Id, new CreateGuildFromTemplateDto { Name = "New Guild" },
            _context, TestPrincipal.Create(UserId), _bus, _auditLog);

        var newGuildId = GetProp<string>(((IValueHttpResult)result).Value!, "Id");
        var roles = _context.Roles.Where(r => r.GuildId == newGuildId).ToList();
        var everyone = roles.Single(r => r.Type == RoleType.Everyone);

        Assert.Multiple(() =>
        {
            Assert.That(everyone.Permissions, Is.EqualTo(Permissions.SendMessages | Role.ExternalEveryoneBaseline),
                "the flagged entry supplies @everyone's mask, not the one merely named 'Everyone'");
            Assert.That(everyone.Permissions.HasFlag(Permissions.ManageGuild), Is.False,
                "the shadowing custom role must not have been mistaken for @everyone");
            Assert.That(roles.Any(r => r.Name == "Everyone" && r.Type == RoleType.None), Is.True,
                "the custom role named 'Everyone' is still replayed as an ordinary role");
        });
    }

    [Test]
    public async Task CreateFromGuild_MarksTheSnapshotEveryoneEntry()
    {
        await SeedManagerMember();
        _context.Roles.Add(Role.CreateEveryoneRole(GuildId, MemberId));
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateFromGuild(GuildId, new CreateGuildTemplateFromGuildDto { Name = "T" },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        var templateId = GetProp<string>(((IValueHttpResult)result).Value!, "Id");
        var snapshot = (await _context.GuildTemplates.AsNoTracking().FirstAsync(t => t.Id == templateId)).Snapshot;

        var flagged = snapshot.Roles.Where(r => r.IsEveryone).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(flagged, Has.Count.EqualTo(1), "exactly one snapshot entry is @everyone");
            Assert.That(flagged[0].Permissions, Is.EqualTo(Role.DefaultEveryonePermissions));
        });
    }

    private async Task<GuildTemplate> SeedTemplate()
    {
        var template = GuildTemplate.Create(new CreateGuildTemplateParams
        {
            Name = "Starter Kit",
            CreatorUserId = OwnerId,
            Snapshot = new TemplateSnapshot
            {
                Roles = [
                    new TemplateRole { Name = "Everyone", Position = 0, Permissions = Permissions.ViewChannel },
                    new TemplateRole { Name = "Moderator", Position = 1, Permissions = Permissions.ManageChannel },
                ],
                Categories = [
                    new TemplateCategory
                    {
                        Name = "Text Channels", Position = 0,
                        Channels = [new TemplateChannel { Name = "general", Type = ChannelType.Text, Position = 0 }],
                    },
                ],
                UncategorizedChannels = [new TemplateChannel { Name = "voice-lounge", Type = ChannelType.Voice, Position = 0 }],
            },
        });
        _context.GuildTemplates.Add(template);
        await _context.SaveChangesAsync();
        return template;
    }

    private static ProfileDto MakeProfile(string userId) => new()
    {
        Id = userId, UserId = userId, UserName = "Tester", Hash = 1234, AvatarUrl = "", BannerUrl = "",
    };

    [Test]
    public async Task CreateGuildFromTemplate_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateGuildFromTemplate("any", new CreateGuildFromTemplateDto { Name = "g" },
            _context, TestPrincipal.CreateAnonymous(), _bus, _auditLog);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateGuildFromTemplate_TemplateDoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.CreateGuildFromTemplate("nonexistent", new CreateGuildFromTemplateDto { Name = "g" },
            _context, TestPrincipal.Create(UserId), _bus, _auditLog);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task CreateGuildFromTemplate_ProfileNotFound_ReturnsBadRequest()
    {
        var template = await SeedTemplate();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = null });

        var result = await _endpoint.CreateGuildFromTemplate(template.Id, new CreateGuildFromTemplateDto { Name = "g" },
            _context, TestPrincipal.Create(UserId), _bus, _auditLog);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateGuildFromTemplate_Valid_ReplaysSnapshotIntoNewGuild()
    {
        var template = await SeedTemplate();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.CreateGuildFromTemplate(template.Id, new CreateGuildFromTemplateDto { Name = "New Guild" },
            _context, TestPrincipal.Create(UserId), _bus, _auditLog);

        var newGuildId = GetProp<string>(((IValueHttpResult)result).Value!, "Id");

        var guild = await _context.Guilds.AsNoTracking().FirstOrDefaultAsync(g => g.Id == newGuildId);
        Assert.That(guild, Is.Not.Null);
        Assert.That(guild!.OwnerId, Is.EqualTo(UserId));

        var roles = _context.Roles.Where(r => r.GuildId == newGuildId).ToList();
        // The snapshot seeded here predates TemplateRole.IsEveryone, so this also covers the
        // name/position fallback.
        Assert.That(roles.Any(r => r.Type == RoleType.Everyone
                                   && r.Permissions == (Permissions.ViewChannel | Role.ExternalEveryoneBaseline)), Is.True,
            "Everyone role's permissions must come from the template snapshot, floored by the baseline");
        Assert.That(roles.Any(r => r.Name == "Moderator" && r.Permissions == Permissions.ManageChannel), Is.True,
            "ordinary template roles are replayed verbatim - the baseline is @everyone-only");

        var categories = _context.Categories.Where(c => c.GuildId == newGuildId).ToList();
        Assert.That(categories, Has.Count.EqualTo(1));

        var channels = _context.Channels.Where(c => c.GuildId == newGuildId).ToList();
        Assert.That(channels.Any(c => c.Name == "general" && c.CategoryId == categories[0].Id), Is.True);
        Assert.That(channels.Any(c => c.Name == "voice-lounge" && c.CategoryId == null), Is.True);

        Assert.That(guild.SystemChannelId, Is.EqualTo(channels.First(c => c.Name == "general").Id),
            "SystemChannelId must point at the first Text channel created from the template");
    }

    [Test]
    public async Task CreateGuildFromTemplate_Valid_IncrementsUsageCount()
    {
        var template = await SeedTemplate();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await _endpoint.CreateGuildFromTemplate(template.Id, new CreateGuildFromTemplateDto { Name = "New Guild" },
            _context, TestPrincipal.Create(UserId), _bus, _auditLog);

        var reloaded = await _context.GuildTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == template.Id);
        Assert.That(reloaded!.UsageCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CreateGuildFromTemplate_Valid_WritesAuditLogEntry()
    {
        var template = await SeedTemplate();
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await _endpoint.CreateGuildFromTemplate(template.Id, new CreateGuildFromTemplateDto { Name = "New Guild" },
            _context, TestPrincipal.Create(UserId), _bus, _auditLog);

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.GuildCreatedFromTemplate).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════
    // R14 - role hierarchy, role metadata and permission overwrites
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Persists a snapshot as a template row and instantiates it, returning the new guild's
    /// id. Used by the R14 tests that arrange a snapshot directly instead of capturing one.</summary>
    private async Task<string> InstantiateAsync(TemplateSnapshot snapshot)
    {
        var template = GuildTemplate.Create(new CreateGuildTemplateParams
        {
            Name = "T", CreatorUserId = OwnerId, Snapshot = snapshot,
        });
        _context.GuildTemplates.Add(template);
        await _context.SaveChangesAsync();

        return await UseTemplateAsync(template.Id);
    }

    /// <summary>Snapshots the seeded source guild and returns the new template's id.</summary>
    private async Task<string> CaptureAsync()
    {
        var result = await _endpoint.CreateFromGuild(GuildId, new CreateGuildTemplateFromGuildDto { Name = "T" },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        return GetProp<string>(((IValueHttpResult)result).Value!, "Id");
    }

    private async Task<TemplateSnapshot> SnapshotOfAsync(string templateId) =>
        (await _context.GuildTemplates.AsNoTracking().FirstAsync(t => t.Id == templateId)).Snapshot;

    private async Task<string> UseTemplateAsync(string templateId)
    {
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.CreateGuildFromTemplate(templateId, new CreateGuildFromTemplateDto { Name = "New Guild" },
            _context, TestPrincipal.Create(UserId), _bus, _auditLog);

        return GetProp<string>(((IValueHttpResult)result).Value!, "Id");
    }

    [Test]
    public async Task CreateGuildFromTemplate_RankedRoles_KeepRelativeOrderAndDoNotCollide()
    {
        // Before R14 every one of these landed at Position 0, on top of @everyone and each other,
        // which collapsed the whole hierarchy in any guild created from a template.
        var newGuildId = await InstantiateAsync(new TemplateSnapshot
        {
            Roles =
            [
                new TemplateRole { Name = "Everyone", Position = 0, IsEveryone = true },
                new TemplateRole { Name = "Member", Position = 3 },
                new TemplateRole { Name = "Admin", Position = 11 },
                new TemplateRole { Name = "Moderator", Position = 7 },
            ],
        });

        var roles = _context.Roles.Where(r => r.GuildId == newGuildId).ToList();
        var positionOf = roles.ToDictionary(r => r.Name, r => r.Position);

        Assert.Multiple(() =>
        {
            Assert.That(positionOf["Everyone"], Is.EqualTo(0), "@everyone stays at the bottom of the hierarchy");
            Assert.That(positionOf["Member"], Is.LessThan(positionOf["Moderator"]));
            Assert.That(positionOf["Moderator"], Is.LessThan(positionOf["Admin"]));
            Assert.That(roles.Select(r => r.Position).Distinct().Count(), Is.EqualTo(roles.Count),
                "no two roles may share a position");
        });
    }

    [Test]
    public async Task CreateGuildFromTemplate_RolesCapturedAtTheSamePosition_AreStillGivenDistinctPositions()
    {
        // A source guild is free to hold two roles at the same position, and an old snapshot holds
        // every role at 0 - so the captured number can only be trusted for relative order.
        var newGuildId = await InstantiateAsync(new TemplateSnapshot
        {
            Roles =
            [
                new TemplateRole { Name = "Everyone", Position = 0, IsEveryone = true },
                new TemplateRole { Name = "Alpha", Position = 4 },
                new TemplateRole { Name = "Beta", Position = 4 },
            ],
        });

        var positions = _context.Roles.Where(r => r.GuildId == newGuildId).Select(r => r.Position).ToList();
        Assert.That(positions.Distinct().Count(), Is.EqualTo(positions.Count));
    }

    [Test]
    public async Task CreateGuildFromTemplate_HouseholdKind_ReusesTheSeededFlatmatesRoleInsteadOfDuplicatingIt()
    {
        // Guild.Create seeds Flatmates at position 1 for a household, and the snapshot carries its
        // own copy - so the naive "renumber from 1" collides with it and the naive "always create"
        // splits the chore rotation pool across two roles with the same name.
        var newGuildId = await InstantiateAsync(new TemplateSnapshot
        {
            Kind = GuildKind.Household,
            Roles =
            [
                new TemplateRole { Name = "Everyone", Position = 0, IsEveryone = true },
                new TemplateRole
                {
                    Name = Role.FlatmatesRoleName, Position = 1, Color = "#123456",
                    ModulePermissions = Role.FlatmatePermissions | ModulePermissions.ManageGuests,
                },
                new TemplateRole { Name = "Guests", Position = 2 },
            ],
        });

        var roles = _context.Roles.Where(r => r.GuildId == newGuildId).ToList();
        var flatmates = roles.Single(r => r.Name == Role.FlatmatesRoleName);

        Assert.Multiple(() =>
        {
            Assert.That(roles.Count(r => r.Name == Role.FlatmatesRoleName), Is.EqualTo(1));
            Assert.That(flatmates.Members, Is.Not.Empty, "the seeded role still holds the owner");
            Assert.That(flatmates.Color, Is.EqualTo("#123456"), "and takes the captured role's settings");
            Assert.That(roles.Select(r => r.Position).Distinct().Count(), Is.EqualTo(roles.Count),
                "the templated roles must stack above whatever Guild.Create already seeded");
        });
    }

    [Test]
    public async Task TemplateRoundTrip_PrivateChannel_StaysPrivateInTheNewGuild()
    {
        await SeedManagerMember();
        var everyone = Role.CreateEveryoneRole(GuildId, MemberId);
        _context.Roles.Add(everyone);
        var staff = Role.Create(new CreateRoleParams { Name = "Staff", GuildId = GuildId });
        staff.Position = 3;
        _context.Roles.Add(staff);

        var category = Category.Create(new CreateCategoryParams { Name = "Staff Area", GuildId = GuildId, Position = 0 });
        _context.Categories.Add(category);
        var secret = Channel.Create(new CreateChannelParams
        {
            Name = "staff-only", Type = ChannelType.Text, GuildId = GuildId, CategoryId = category.Id,
            Description = "", Position = 0,
        });
        _context.Channels.Add(secret);
        _context.Set<ChannelPermission>().AddRange(
            new ChannelPermission
            {
                Id = ChannelPermission.GenerateId(), ChannelId = secret.Id, RoleId = everyone.Id,
                DenyPermissions = Permissions.ViewChannel,
            },
            new ChannelPermission
            {
                Id = ChannelPermission.GenerateId(), ChannelId = secret.Id, RoleId = staff.Id,
                AllowPermissions = Permissions.ViewChannel,
            });
        await _context.SaveChangesAsync();

        var newGuildId = await UseTemplateAsync(await CaptureAsync());

        var newChannel = _context.Channels.Single(c => c.GuildId == newGuildId && c.Name == "staff-only");
        var newEveryoneId = _context.Roles.Single(r => r.GuildId == newGuildId && r.Type == RoleType.Everyone).Id;
        var newStaffId = _context.Roles.Single(r => r.GuildId == newGuildId && r.Name == "Staff").Id;
        var replayed = _context.Set<ChannelPermission>().Where(p => p.ChannelId == newChannel.Id).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(replayed, Has.Count.EqualTo(2));
            Assert.That(replayed.Single(p => p.RoleId == newEveryoneId).DenyPermissions.HasFlag(Permissions.ViewChannel),
                Is.True, "without the @everyone deny the channel is public in the new guild");
            Assert.That(replayed.Single(p => p.RoleId == newStaffId).AllowPermissions.HasFlag(Permissions.ViewChannel),
                Is.True, "and without the staff allow nobody can see it at all");
        });
    }

    [Test]
    public async Task TemplateRoundTrip_CategoryOverwrites_AreCapturedAndReplayed()
    {
        await SeedManagerMember();
        var everyone = Role.CreateEveryoneRole(GuildId, MemberId);
        _context.Roles.Add(everyone);
        var category = Category.Create(new CreateCategoryParams { Name = "Private Area", GuildId = GuildId, Position = 0 });
        _context.Categories.Add(category);
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = ChannelPermission.GenerateId(), CategoryId = category.Id, RoleId = everyone.Id,
            DenyPermissions = Permissions.ViewChannel,
        });
        await _context.SaveChangesAsync();

        var templateId = await CaptureAsync();
        Assert.That((await SnapshotOfAsync(templateId)).Categories.Single().Overwrites, Has.Count.EqualTo(1));

        var newGuildId = await UseTemplateAsync(templateId);
        var newCategoryId = _context.Categories.Single(c => c.GuildId == newGuildId).Id;
        var replayed = _context.Set<ChannelPermission>().Single(p => p.CategoryId == newCategoryId);

        Assert.That(replayed.DenyPermissions.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task CreateFromGuild_MemberTargetedOverwrite_IsNotCaptured()
    {
        await SeedManagerMember();
        _context.Roles.Add(Role.CreateEveryoneRole(GuildId, MemberId));
        var channel = Channel.Create(new CreateChannelParams
        {
            Name = "general", Type = ChannelType.Text, GuildId = GuildId, Description = "", Position = 0,
        });
        _context.Channels.Add(channel);
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = ChannelPermission.GenerateId(), ChannelId = channel.Id, MemberId = MemberId,
            AllowPermissions = Permissions.ManageChannel,
        });
        await _context.SaveChangesAsync();

        var snapshot = await SnapshotOfAsync(await CaptureAsync());

        Assert.That(snapshot.UncategorizedChannels.Single().Overwrites, Is.Empty,
            "a new guild has no member for a per-member overwrite to attach to");
    }

    [Test]
    public async Task CreateGuildFromTemplate_OverwriteNamingAnUnknownRole_IsDroppedWithoutFailing()
    {
        var newGuildId = await InstantiateAsync(new TemplateSnapshot
        {
            Roles = [new TemplateRole { Name = "Everyone", Position = 0, IsEveryone = true }],
            UncategorizedChannels =
            [
                new TemplateChannel
                {
                    Name = "general", Type = ChannelType.Text, Position = 0,
                    Overwrites =
                    [
                        new TemplateOverwrite { RoleName = "Deleted Role", Deny = Permissions.ViewChannel },
                        new TemplateOverwrite { RoleName = "Everyone", Deny = Permissions.SendMessages },
                    ],
                },
            ],
        });

        var channelId = _context.Channels.Single(c => c.GuildId == newGuildId).Id;
        var replayed = _context.Set<ChannelPermission>().Where(p => p.ChannelId == channelId).ToList();

        Assert.That(replayed, Has.Count.EqualTo(1));
        Assert.That(replayed[0].DenyPermissions, Is.EqualTo(Permissions.SendMessages));
    }

    [Test]
    public async Task TemplateRoundTrip_RolePermissions_SurviveInBothMasks()
    {
        await SeedManagerMember();
        _context.Roles.Add(Role.CreateEveryoneRole(GuildId, MemberId));
        _context.Roles.Add(Role.Create(new CreateRoleParams
        {
            Name = "Librarian", GuildId = GuildId,
            Permissions = Permissions.ManageEvents | Permissions.PinMessages,
            ModulePermissions = ModulePermissions.EditAnyWikiPage | ModulePermissions.ManageLists,
        }));
        await _context.SaveChangesAsync();

        var templateId = await CaptureAsync();
        var captured = (await SnapshotOfAsync(templateId)).Roles.Single(r => r.Name == "Librarian");
        Assert.Multiple(() =>
        {
            Assert.That(captured.Permissions, Is.EqualTo(Permissions.ManageEvents | Permissions.PinMessages));
            Assert.That(captured.ModulePermissions,
                Is.EqualTo(ModulePermissions.EditAnyWikiPage | ModulePermissions.ManageLists),
                "a template that captures only the core mask silently drops every wiki and household bit");
        });

        var newGuildId = await UseTemplateAsync(templateId);
        var replayed = _context.Roles.Single(r => r.GuildId == newGuildId && r.Name == "Librarian");

        Assert.Multiple(() =>
        {
            Assert.That(replayed.Permissions, Is.EqualTo(Permissions.ManageEvents | Permissions.PinMessages));
            Assert.That(replayed.ModulePermissions,
                Is.EqualTo(ModulePermissions.EditAnyWikiPage | ModulePermissions.ManageLists));
        });
    }

    [Test]
    public async Task TemplateRoundTrip_EveryoneModuleMask_SurvivesFlooredByTheBaseline()
    {
        await SeedManagerMember();
        var everyone = Role.CreateEveryoneRole(GuildId, MemberId);
        everyone.ApplyExternalEveryonePermissions(Permissions.ViewChannel,
            ModulePermissions.PublishWikiPublicly);
        _context.Roles.Add(everyone);
        await _context.SaveChangesAsync();

        var newGuildId = await UseTemplateAsync(await CaptureAsync());
        var replayed = _context.Roles.Single(r => r.GuildId == newGuildId && r.Type == RoleType.Everyone);

        Assert.That(replayed.ModulePermissions.HasFlag(ModulePermissions.PublishWikiPublicly), Is.True,
            "the captured module mask must reach the new guild's @everyone role");
        Assert.That(replayed.ModulePermissions.HasFlag(ModulePermissions.ViewWiki), Is.True,
            "and the baseline the source cannot express is still restored on top");
    }

    [Test]
    public async Task TemplateRoundTrip_RoleMetadata_CarriesDisplayFieldsAndDropsIntegrationOwnership()
    {
        await SeedManagerMember();
        _context.Roles.Add(Role.CreateEveryoneRole(GuildId, MemberId));
        var bots = Role.Create(new CreateRoleParams
        {
            Name = "Bots", GuildId = GuildId, Description = "Anything that isn't a person",
            Hoist = true, Mentionable = false, UnicodeEmoji = "\U0001F916",
        });
        bots.IsManaged = true;
        bots.BotUserId = "usr_bot";
        bots.IntegrationId = "intg_1";
        _context.Roles.Add(bots);
        await _context.SaveChangesAsync();

        var newGuildId = await UseTemplateAsync(await CaptureAsync());
        var replayed = _context.Roles.Single(r => r.GuildId == newGuildId && r.Name == "Bots");

        Assert.Multiple(() =>
        {
            Assert.That(replayed.Description, Is.EqualTo("Anything that isn't a person"));
            Assert.That(replayed.Hoist, Is.True);
            Assert.That(replayed.Mentionable, Is.False);
            Assert.That(replayed.UnicodeEmoji, Is.EqualTo("\U0001F916"));

            Assert.That(replayed.IsManaged, Is.False,
                "there is no integration behind this role in the new guild, so nothing may make it uneditable");
            Assert.That(replayed.BotUserId, Is.Null);
            Assert.That(replayed.IntegrationId, Is.Null);
        });
    }

    [Test]
    public async Task CreateFromGuild_RoleIcon_IsNotTemplated()
    {
        await SeedManagerMember();
        _context.Roles.Add(Role.CreateEveryoneRole(GuildId, MemberId));
        var vip = Role.Create(new CreateRoleParams
        {
            Name = "VIP", GuildId = GuildId, IconUrl = "https://cdn.example/role-icons/1/abc.png",
        });
        _context.Roles.Add(vip);
        await _context.SaveChangesAsync();

        var newGuildId = await UseTemplateAsync(await CaptureAsync());
        var replayed = _context.Roles.Single(r => r.GuildId == newGuildId && r.Name == "VIP");

        Assert.That(replayed.IconUrl, Is.Null,
            "the icon is an asset of the source guild - a new guild's owner neither owns it nor can replace it");
    }

    [Test]
    public async Task CreateGuildFromTemplate_SnapshotCapturedUnderTheOldShape_InstantiatesWithSensibleDefaults()
    {
        // Arranged as a stale row rather than by capturing one: a freshly written snapshot always
        // carries every field, so the absent-field branch is untested by construction otherwise.
        var newGuildId = await InstantiateAsync(new TemplateSnapshot
        {
            Roles =
            [
                new TemplateRole { Name = "Everyone", Position = 0, Permissions = Permissions.ViewChannel },
                new TemplateRole { Name = "Moderator", Position = 0, Permissions = Permissions.ManageChannel },
            ],
            Categories =
            [
                new TemplateCategory
                {
                    Name = "Text Channels", Position = 0,
                    Channels = [new TemplateChannel { Name = "general", Type = ChannelType.Text, Position = 0 }],
                },
            ],
        });

        var everyone = _context.Roles.Single(r => r.GuildId == newGuildId && r.Type == RoleType.Everyone);
        var moderator = _context.Roles.Single(r => r.GuildId == newGuildId && r.Name == "Moderator");
        var channelId = _context.Channels.Single(c => c.GuildId == newGuildId).Id;

        Assert.Multiple(() =>
        {
            // The IsEveryone flag is absent too, so this also re-covers the name/position fallback.
            Assert.That(everyone.Permissions, Is.EqualTo(Permissions.ViewChannel | Role.ExternalEveryoneBaseline));
            Assert.That(everyone.ModulePermissions, Is.EqualTo(Role.ExternalEveryoneModuleBaseline),
                "an absent module mask floors to the baseline rather than leaving @everyone with nothing");

            Assert.That(moderator.Permissions, Is.EqualTo(Permissions.ManageChannel));
            Assert.That(moderator.ModulePermissions, Is.EqualTo(ModulePermissions.None));
            Assert.That(moderator.Description, Is.Null);
            Assert.That(moderator.Hoist, Is.False);
            Assert.That(moderator.UnicodeEmoji, Is.Null);
            Assert.That(moderator.Mentionable, Is.True,
                "an absent Mentionable must read as Role's own default, not as a silent mute");
            Assert.That(moderator.Position, Is.GreaterThan(0),
                "even an old snapshot's all-zero positions must not collide with @everyone");

            Assert.That(_context.Set<ChannelPermission>().Any(p => p.ChannelId == channelId), Is.False,
                "no overwrites were captured, so none are replayed - the channel is simply public");
        });
    }
}
