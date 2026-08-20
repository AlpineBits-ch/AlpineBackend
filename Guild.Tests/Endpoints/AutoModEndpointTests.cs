using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers AutoModEndpoint's GET/PUT config actions, including ManageGuild permission gating,
/// input validation, and the upsert-not-just-update behavior of UpdateConfig.
/// </summary>
[TestFixture]
public class AutoModEndpointTests
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
    private AutoModEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = PermissionTestFactory.Create(_cache, _context);
        _auditLog = new AuditLogService(_context);
        _bus = new FakeMessageBus();
        _endpoint = new AutoModEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedManagerMember()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageGuild, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    private async Task SeedPlainMember()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════ GetConfig
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetConfig_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.GetConfig(GuildId, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetConfig_LacksManageGuild_ReturnsForbid()
    {
        await SeedPlainMember();
        var result = await _endpoint.GetConfig(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetConfig_NoRowYet_ReturnsDisabledDefaults()
    {
        await SeedManagerMember();

        var result = await _endpoint.GetConfig(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<UpdateAutoModConfigDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ok!.Value!.Enabled, Is.False);
            Assert.That(ok.Value.BlockedWords, Is.Empty);
        });
    }

    [Test]
    public async Task GetConfig_ExistingRow_ReturnsItsValues()
    {
        await SeedManagerMember();
        _context.Set<GuildAutoModConfig>().Add(new GuildAutoModConfig
        {
            GuildId = GuildId, Enabled = true, BlockedWords = ["spam"], MaxMessagesPerInterval = 3, IntervalSeconds = 5, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetConfig(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<UpdateAutoModConfigDto>;
        Assert.That(ok!.Value!.Enabled, Is.True);
        Assert.That(ok.Value.BlockedWords, Is.EquivalentTo(new[] { "spam" }));
    }

    // ══════════════════════════════════════════════════════════════════════ UpdateConfig
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateConfig_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.UpdateConfig(GuildId, new UpdateAutoModConfigDto { Enabled = false },
            _permissionService, _context, _auditLog, _bus, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UpdateConfig_LacksManageGuild_ReturnsForbid()
    {
        await SeedPlainMember();
        var result = await _endpoint.UpdateConfig(GuildId, new UpdateAutoModConfigDto { Enabled = false },
            _permissionService, _context, _auditLog, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task UpdateConfig_NonPositiveMaxMessagesPerInterval_ReturnsBadRequest(int value)
    {
        await SeedManagerMember();
        var dto = new UpdateAutoModConfigDto { Enabled = true, MaxMessagesPerInterval = value, IntervalSeconds = 10 };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _context, _auditLog, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task UpdateConfig_NonPositiveIntervalSeconds_ReturnsBadRequest(int value)
    {
        await SeedManagerMember();
        var dto = new UpdateAutoModConfigDto { Enabled = true, MaxMessagesPerInterval = 5, IntervalSeconds = value };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _context, _auditLog, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_NoExistingRow_CreatesNewConfig()
    {
        await SeedManagerMember();
        var dto = new UpdateAutoModConfigDto { Enabled = true, BlockedWords = ["foo", "  ", "bar"], MaxMessagesPerInterval = 4, IntervalSeconds = 8 };

        var result = await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _context, _auditLog, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<UpdateAutoModConfigDto>>());
        var config = _context.Set<GuildAutoModConfig>().Find(GuildId);
        Assert.That(config, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(config!.Enabled, Is.True);
            // Blank entries are stripped and remaining ones trimmed.
            Assert.That(config.BlockedWords, Is.EquivalentTo(new[] { "foo", "bar" }));
            Assert.That(config.MaxMessagesPerInterval, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task UpdateConfig_ExistingRow_UpdatesInPlace()
    {
        await SeedManagerMember();
        _context.Set<GuildAutoModConfig>().Add(new GuildAutoModConfig { GuildId = GuildId, Enabled = false, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var dto = new UpdateAutoModConfigDto { Enabled = true, BlockedWords = ["x"] };
        await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _context, _auditLog, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var configCount = _context.Set<GuildAutoModConfig>().Count(c => c.GuildId == GuildId);
        Assert.That(configCount, Is.EqualTo(1), "Must update the existing row, not insert a second one");
        var config = _context.Set<GuildAutoModConfig>().Find(GuildId);
        Assert.That(config!.Enabled, Is.True);
    }

    [Test]
    public async Task UpdateConfig_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();
        await _endpoint.UpdateConfig(GuildId, new UpdateAutoModConfigDto { Enabled = true }, _permissionService, _context, _auditLog, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.GuildId == GuildId).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].ActionType, Is.EqualTo(AuditActionType.AutoModConfigUpdated));
    }

    [Test]
    public async Task UpdateConfig_Valid_PublishesConfigChangedForEveryChannelOfTheGuild()
    {
        await SeedManagerMember();
        _context.Channels.Add(new Channel { Id = "chan-1", GuildId = GuildId, Name = "general", Type = ChannelType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.Channels.Add(new Channel { Id = "chan-2", GuildId = GuildId, Name = "off-topic", Type = ChannelType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.Channels.Add(new Channel { Id = "other-guild-chan", GuildId = "guild-2", Name = "elsewhere", Type = ChannelType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        await _endpoint.UpdateConfig(GuildId, new UpdateAutoModConfigDto { Enabled = false }, _permissionService, _context, _auditLog, _bus, TestPrincipal.Create(UserId));

        var published = _bus.Published.OfType<AutoModConfigChanged>().SingleOrDefault();
        Assert.That(published, Is.Not.Null, "Messaging has to be told, or it keeps enforcing the old rules from its cache");
        Assert.That(published!.ChannelIds, Is.EquivalentTo(new[] { "chan-1", "chan-2" }));
    }

    [Test]
    public async Task UpdateConfig_Rejected_PublishesNothing()
    {
        await SeedManagerMember();
        var dto = new UpdateAutoModConfigDto { Enabled = true, MaxMessagesPerInterval = 0, IntervalSeconds = 10 };

        await _endpoint.UpdateConfig(GuildId, dto, _permissionService, _context, _auditLog, _bus, TestPrincipal.Create(UserId));

        Assert.That(_bus.Published, Is.Empty);
    }
}
