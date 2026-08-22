using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints.Guild;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers GuildEndpoint.UpdateGuild for PrimaryLanguage and OtherLanguages: normalize-and-store,
/// reject-without-mutating, and the null-means-unchanged contract every field on this DTO carries.
/// </summary>
[TestFixture]
public class GuildLanguageUpdateTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private MfaElevationService _mfa = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private GuildEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _permissionService = PermissionTestFactory.Create(new FakeDistributedCache(), _context);
        _auditLog = new AuditLogService(_context);
        _mfa = new MfaElevationService(_context);
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new GuildEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedManagerMember()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageGuild,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Declaring_languages_normalizes_and_stores_both()
    {
        await SeedManagerMember();

        var result = await _endpoint.UpdateGuild(
            GuildId, new UpdateGuildDto { PrimaryLanguage = "PT-br", OtherLanguages = ["EN", "pt-BR"] },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<GuildDto>>());
        var reloaded = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.PrimaryLanguage, Is.EqualTo("pt-BR"));
            Assert.That(reloaded.OtherLanguages, Is.EqualTo(new List<string> { "en" }));
        });
    }

    [Test]
    public async Task A_malformed_tag_is_refused_without_touching_the_guild()
    {
        await SeedManagerMember();

        var result = await _endpoint.UpdateGuild(
            GuildId, new UpdateGuildDto { PrimaryLanguage = "nope!" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        var reloaded = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.That(reloaded.PrimaryLanguage, Is.EqualTo("en"), "the rejected value must not have been written");
    }

    /// <summary>
    /// A refusal returns normally, so Wolverine's AutoApplyTransactions commits whatever the
    /// handler already assigned. Every field on this PATCH has to be validated before the first
    /// mutation, not beside its own assignment.
    /// </summary>
    [Test]
    public async Task A_refusal_anywhere_leaves_every_other_field_untouched()
    {
        await SeedManagerMember();

        var result = await _endpoint.UpdateGuild(
            GuildId,
            new UpdateGuildDto
            {
                Name = "renamed",
                PrimaryLanguage = "de",
                DefaultMessageNotifications = NotificationLevel.Nothing,
            },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        var reloaded = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Name, Is.EqualTo("Test Guild"));
            Assert.That(reloaded.PrimaryLanguage, Is.EqualTo("en"));
        });
    }

    [Test]
    public async Task Omitting_the_fields_leaves_them_alone()
    {
        await SeedManagerMember();
        var guild = _context.Guilds.Single(g => g.Id == GuildId);
        guild.PrimaryLanguage = "fr";
        guild.OtherLanguages = ["de"];
        await _context.SaveChangesAsync();

        await _endpoint.UpdateGuild(
            GuildId, new UpdateGuildDto { Name = "renamed" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService, _mfa);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.PrimaryLanguage, Is.EqualTo("fr"));
            Assert.That(reloaded.OtherLanguages, Is.EqualTo(new List<string> { "de" }));
        });
    }
}
