using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers ScheduledEventEndpoint's List/Create/Update/Cancel and interest-toggle actions against
/// an EF Core InMemory database, exercising permission gating (ManageEvents) and the
/// soft-cancel-not-hard-delete behavior for CancelEvent.
/// </summary>
[TestFixture]
public class ScheduledEventEndpointTests
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
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private ScheduledEventEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new ScheduledEventEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild(string ownerId = OwnerId) => new()
    {
        Id = GuildId, OwnerId = ownerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedMemberWithPermission(Permissions permission)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "manager", Type = RoleType.None, Permissions = permission,
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

    private async Task SeedPlainMember()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        await _context.SaveChangesAsync();
    }

    private async Task<GuildScheduledEvent> SeedEvent(string guildId = GuildId, string creatorId = UserId)
    {
        var evt = GuildScheduledEvent.Create(new CreateGuildScheduledEventParams
        {
            GuildId = guildId,
            CreatorUserId = creatorId,
            Title = "Movie night",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        _context.Set<GuildScheduledEvent>().Add(evt);
        await _context.SaveChangesAsync();
        return evt;
    }

    // ══════════════════════════════════════════════════════════════════════ ListEvents
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ListEvents_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.ListEvents(GuildId, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task ListEvents_NotAMember_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _endpoint.ListEvents(GuildId, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ListEvents_ExcludesCancelledEvents_AndOrdersByStartsAt()
    {
        await SeedPlainMember();
        var later = GuildScheduledEvent.Create(new CreateGuildScheduledEventParams
        {
            GuildId = GuildId, CreatorUserId = UserId, Title = "Later", StartsAt = DateTimeOffset.UtcNow.AddDays(5),
        });
        var sooner = GuildScheduledEvent.Create(new CreateGuildScheduledEventParams
        {
            GuildId = GuildId, CreatorUserId = UserId, Title = "Sooner", StartsAt = DateTimeOffset.UtcNow.AddDays(1),
        });
        var cancelled = GuildScheduledEvent.Create(new CreateGuildScheduledEventParams
        {
            GuildId = GuildId, CreatorUserId = UserId, Title = "Cancelled", StartsAt = DateTimeOffset.UtcNow.AddDays(2),
        });
        cancelled.Status = GuildScheduledEventStatus.Cancelled;
        _context.Set<GuildScheduledEvent>().AddRange(later, sooner, cancelled);
        await _context.SaveChangesAsync();

        var result = await _endpoint.ListEvents(GuildId, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<IEnumerable<Guild.Application.Dtos.Response.GuildScheduledEventDto>>;
        Assert.That(ok, Is.Not.Null);
        var titles = ok!.Value!.Select(e => e.Title).ToList();
        Assert.That(titles, Is.EqualTo(new[] { "Sooner", "Later" }));
    }

    // ══════════════════════════════════════════════════════════════════════ CreateEvent
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateEvent_Unauthenticated_ReturnsUnauthorized()
    {
        var dto = new CreateScheduledEventDto { Title = "x", StartsAt = DateTimeOffset.UtcNow.AddDays(1) };
        var result = await _endpoint.CreateEvent(GuildId, dto, _permissionService, _context, _auditLog, _hub,
            _hydrateService, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateEvent_LacksManageEvents_ReturnsForbid()
    {
        await SeedPlainMember();
        var dto = new CreateScheduledEventDto { Title = "x", StartsAt = DateTimeOffset.UtcNow.AddDays(1) };

        var result = await _endpoint.CreateEvent(GuildId, dto, _permissionService, _context, _auditLog, _hub,
            _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateEvent_MissingTitle_ReturnsBadRequest()
    {
        await SeedMemberWithPermission(Permissions.ManageEvents);
        var dto = new CreateScheduledEventDto { Title = "  ", StartsAt = DateTimeOffset.UtcNow.AddDays(1) };

        var result = await _endpoint.CreateEvent(GuildId, dto, _permissionService, _context, _auditLog, _hub,
            _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateEvent_EndsAtBeforeStartsAt_ReturnsBadRequest()
    {
        await SeedMemberWithPermission(Permissions.ManageEvents);
        var starts = DateTimeOffset.UtcNow.AddDays(2);
        var dto = new CreateScheduledEventDto { Title = "x", StartsAt = starts, EndsAt = starts.AddHours(-1) };

        var result = await _endpoint.CreateEvent(GuildId, dto, _permissionService, _context, _auditLog, _hub,
            _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateEvent_Valid_PersistsAndReturnsDto()
    {
        await SeedMemberWithPermission(Permissions.ManageEvents);
        var starts = DateTimeOffset.UtcNow.AddDays(1);
        var dto = new CreateScheduledEventDto { Title = "Raid night", Description = "bring pots", StartsAt = starts };

        var result = await _endpoint.CreateEvent(GuildId, dto, _permissionService, _context, _auditLog, _hub,
            _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.GuildScheduledEventDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(ok!.Value!.Title, Is.EqualTo("Raid night"));

        var persisted = await _context.Set<GuildScheduledEvent>().FindAsync(ok.Value.Id);
        Assert.That(persisted, Is.Not.Null);
    }

    [Test]
    public async Task CreateEvent_Valid_WritesAuditLogEntry()
    {
        await SeedMemberWithPermission(Permissions.ManageEvents);
        var dto = new CreateScheduledEventDto { Title = "x", StartsAt = DateTimeOffset.UtcNow.AddDays(1) };

        await _endpoint.CreateEvent(GuildId, dto, _permissionService, _context, _auditLog, _hub,
            _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.GuildId == GuildId).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].ActionType, Is.EqualTo(AuditActionType.ScheduledEventCreated));
    }

    // ══════════════════════════════════════════════════════════════════════ UpdateEvent
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateEvent_EventDoesNotExist_ReturnsNotFound()
    {
        await SeedMemberWithPermission(Permissions.ManageEvents);
        var result = await _endpoint.UpdateEvent("nonexistent", new UpdateScheduledEventDto { Title = "x" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateEvent_LacksManageEvents_ReturnsForbid()
    {
        await SeedPlainMember();
        var evt = await SeedEvent();

        var result = await _endpoint.UpdateEvent(evt.Id, new UpdateScheduledEventDto { Title = "new" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateEvent_Valid_UpdatesOnlyProvidedFields()
    {
        await SeedMemberWithPermission(Permissions.ManageEvents);
        var evt = await SeedEvent();
        var originalStartsAt = evt.StartsAt;

        var result = await _endpoint.UpdateEvent(evt.Id, new UpdateScheduledEventDto { Title = "New title" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.GuildScheduledEventDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(ok!.Value!.Title, Is.EqualTo("New title"));

        var reloaded = await _context.Set<GuildScheduledEvent>().FindAsync(evt.Id);
        Assert.That(reloaded!.StartsAt, Is.EqualTo(originalStartsAt), "Unset fields must be left untouched");
    }

    // ══════════════════════════════════════════════════════════════════════
    // CancelEvent - soft-delete, not hard-delete
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CancelEvent_EventDoesNotExist_ReturnsNotFound()
    {
        await SeedMemberWithPermission(Permissions.ManageEvents);
        var result = await _endpoint.CancelEvent("nonexistent", _permissionService, _context, _auditLog, _hub,
            _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task CancelEvent_LacksManageEvents_ReturnsForbid()
    {
        await SeedPlainMember();
        var evt = await SeedEvent();

        var result = await _endpoint.CancelEvent(evt.Id, _permissionService, _context, _auditLog, _hub,
            _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CancelEvent_Valid_SoftCancels_RowStillExists()
    {
        await SeedMemberWithPermission(Permissions.ManageEvents);
        var evt = await SeedEvent();

        var result = await _endpoint.CancelEvent(evt.Id, _permissionService, _context, _auditLog, _hub,
            _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());

        var reloaded = await _context.Set<GuildScheduledEvent>().FindAsync(evt.Id);
        Assert.That(reloaded, Is.Not.Null, "Cancel must not hard-delete the row");
        Assert.That(reloaded!.Status, Is.EqualTo(GuildScheduledEventStatus.Cancelled));
    }

    // ══════════════════════════════════════════════════════════════════════ MarkInterested /
    // RemoveInterested ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MarkInterested_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.MarkInterested("any", _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task MarkInterested_EventDoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.MarkInterested("nonexistent", _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task MarkInterested_NotAMember_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        var evt = await SeedEvent(creatorId: OwnerId);

        var result = await _endpoint.MarkInterested(evt.Id, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task MarkInterested_Valid_AddsInterestRecord()
    {
        await SeedPlainMember();
        var evt = await SeedEvent();

        var result = await _endpoint.MarkInterested(evt.Id, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok>());
        var interest = _context.Set<GuildScheduledEventInterest>()
            .FirstOrDefault(i => i.EventId == evt.Id && i.UserId == UserId);
        Assert.That(interest, Is.Not.Null);
    }

    [Test]
    public async Task MarkInterested_AlreadyInterested_IsIdempotent()
    {
        await SeedPlainMember();
        var evt = await SeedEvent();

        await _endpoint.MarkInterested(evt.Id, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();
        await _endpoint.MarkInterested(evt.Id, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var count = _context.Set<GuildScheduledEventInterest>().Count(i => i.EventId == evt.Id && i.UserId == UserId);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task RemoveInterested_Valid_RemovesRecord()
    {
        await SeedPlainMember();
        var evt = await SeedEvent();
        await _endpoint.MarkInterested(evt.Id, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var result = await _endpoint.RemoveInterested(evt.Id, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok>());
        var interest = _context.Set<GuildScheduledEventInterest>()
            .FirstOrDefault(i => i.EventId == evt.Id && i.UserId == UserId);
        Assert.That(interest, Is.Null);
    }

    [Test]
    public async Task RemoveInterested_NoExistingRecord_DoesNotThrow_ReturnsOk()
    {
        var result = await _endpoint.RemoveInterested("nonexistent", _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<Ok>());
    }
}
