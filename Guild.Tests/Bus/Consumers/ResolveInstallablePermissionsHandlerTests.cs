using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus.Consumers;

/// <summary>
/// Covers the bot-install permission gate: caller must hold ManageGuild, and the
/// requested bitmask is clamped down to what the caller actually holds.
/// </summary>
[TestFixture]
public class ResolveInstallablePermissionsHandlerTests
{
    private const string GuildId = "guild-1";
    private const string InstallerId = "installer-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private string _dbName = null!;
    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(_dbName);
        _service = PermissionTestFactory.Create(_cache, _context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild(string ownerId) => new()
    {
        Id = GuildId, OwnerId = ownerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Role MakeRole(Permissions permissions) => new()
    {
        Id = RoleId, GuildId = GuildId, Type = RoleType.None, Name = "installer-role",
        Permissions = permissions,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static GuildMember MakeGuildMember() => new()
    {
        Id = MemberId, GuildId = GuildId, UserId = InstallerId,
        JoinedAt = DateTime.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        SearchValue = $"{InstallerId}#{GuildId}",
    };

    private static RoleMember MakeRoleMember() => new()
    {
        Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task Handle_InstallerLacksManageGuild_ReturnsNotAllowedAndZeroPermissions()
    {
        _context.Guilds.Add(MakeGuild(ownerId: "someone-else"));
        _context.Roles.Add(MakeRole(Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember());
        await _context.SaveChangesAsync();

        var response = await ResolveInstallablePermissionsHandler.Handle(
            new ResolveInstallablePermissionsRequest
            {
                InstallerUserId = InstallerId,
                GuildId = GuildId,
                RequestedPermissions = (ulong)Permissions.SendMessages,
            },
            _service);

        Assert.Multiple(() =>
        {
            Assert.That(response.HasManageGuild, Is.False);
            Assert.That(response.ClampedPermissions, Is.EqualTo(0ul));
        });
    }

    [Test]
    public async Task Handle_InstallerIsOwner_ClampsToExactlyWhatWasRequested()
    {
        _context.Guilds.Add(MakeGuild(ownerId: InstallerId));
        await _context.SaveChangesAsync();

        var requested = Permissions.SendMessages | Permissions.BanMembers;
        var response = await ResolveInstallablePermissionsHandler.Handle(
            new ResolveInstallablePermissionsRequest
            {
                InstallerUserId = InstallerId,
                GuildId = GuildId,
                RequestedPermissions = (ulong)requested,
            },
            _service);

        Assert.Multiple(() =>
        {
            Assert.That(response.HasManageGuild, Is.True);
            Assert.That(response.ClampedPermissions, Is.EqualTo((ulong)requested));
        });
    }

    [Test]
    public async Task Handle_InstallerHasManageGuildButNotOtherBits_ClampsRequest()
    {
        _context.Guilds.Add(MakeGuild(ownerId: "someone-else"));
        _context.Roles.Add(MakeRole(Permissions.ManageGuild | Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember());
        await _context.SaveChangesAsync();

        var response = await ResolveInstallablePermissionsHandler.Handle(
            new ResolveInstallablePermissionsRequest
            {
                InstallerUserId = InstallerId,
                GuildId = GuildId,
                RequestedPermissions = (ulong)(Permissions.SendMessages | Permissions.BanMembers),
            },
            _service);

        Assert.Multiple(() =>
        {
            Assert.That(response.HasManageGuild, Is.True);
            Assert.That((Permissions)response.ClampedPermissions, Is.EqualTo(Permissions.SendMessages),
                "BanMembers must be clamped away since the installer doesn't hold it");
        });
    }
}
