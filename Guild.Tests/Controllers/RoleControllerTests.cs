using Guild.Application.Controllers;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Controllers;

/// <summary>
/// Covers RoleController.GetRoleMembersAsync: the guild-membership gate and the page cap.
/// </summary>
[TestFixture]
public class RoleControllerTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private RoleController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = PermissionTestFactory.Create(_cache, _context);
        _controller = new RoleController(_context, _permissionService, NullLogger<GuildController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = TestPrincipal.Create(UserId) } },
        };
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Seeds the guild, a viewer holding the role, and <paramref name="extraMembers"/>
    /// further members of the same role.</summary>
    private async Task SeedAsync(int extraMembers = 0)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "role", Permissions = Permissions.ViewChannel,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-0", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        for (var i = 1; i <= extraMembers; i++)
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = $"extra-member-{i}", GuildId = GuildId, UserId = $"extra-user-{i}", JoinedAt = DateTime.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"extra-user-{i}#{GuildId}",
            });
            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-{i:D4}", RoleId = RoleId, MemberId = $"extra-member-{i}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task GetRoleMembers_Unauthenticated_ReturnsUnauthorized()
    {
        _controller.ControllerContext.HttpContext.User = TestPrincipal.CreateAnonymous();

        var result = await _controller.GetRoleMembersAsync(RoleId);
        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task GetRoleMembers_UnknownRole_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await _controller.GetRoleMembersAsync("nonexistent");
        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetRoleMembers_NonMember_ReturnsForbid()
    {
        await SeedAsync();
        _controller.ControllerContext.HttpContext.User = TestPrincipal.Create("stranger");

        var result = await _controller.GetRoleMembersAsync(RoleId);
        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task GetRoleMembers_Member_ReturnsThePage()
    {
        await SeedAsync(extraMembers: 4);

        var result = await _controller.GetRoleMembersAsync(RoleId, take: 3) as OkObjectResult;
        var page = result!.Value as IEnumerable<RoleMemberDto>;

        Assert.That(page!.Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task GetRoleMembers_OverLargeTake_IsClampedToThePageCap()
    {
        await SeedAsync(extraMembers: 120);

        var result = await _controller.GetRoleMembersAsync(RoleId, take: int.MaxValue) as OkObjectResult;
        var page = result!.Value as IEnumerable<RoleMemberDto>;

        Assert.That(page!.Count(), Is.EqualTo(100));
    }

    [Test]
    public async Task GetRoleMembers_NegativeTakeOrSkip_DoesNotThrow()
    {
        await SeedAsync(extraMembers: 2);

        var result = await _controller.GetRoleMembersAsync(RoleId, take: -5, skip: -5) as OkObjectResult;
        var page = result!.Value as IEnumerable<RoleMemberDto>;

        Assert.That(page!.Count(), Is.EqualTo(1), "a negative take clamps to the smallest legal page");
    }
}
