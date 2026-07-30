using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
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
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
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

    // ══════════════════════════════════════════════════════════════════════
    // CreateFromGuild
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
            // NOTE: order is intentionally not asserted here - the EF Core InMemory provider does
            // not reliably preserve List<T> order for OwnsMany/ToJson owned collections across a
            // save/reload round-trip (unlike a real Postgres jsonb column, which stores the exact
            // JSON array text as built by the endpoint, Insert(0, ...) included).
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

    // ══════════════════════════════════════════════════════════════════════
    // GetTemplate
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
        Assert.That(roles.Any(r => r.Type == RoleType.Everyone && r.Permissions == Permissions.ViewChannel), Is.True,
            "Everyone role's permissions must come from the template snapshot");
        Assert.That(roles.Any(r => r.Name == "Moderator" && r.Permissions == Permissions.ManageChannel), Is.True);

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
}
