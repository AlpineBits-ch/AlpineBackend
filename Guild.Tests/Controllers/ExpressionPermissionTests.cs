using System.Text;
using Amazon.S3;
using Guild.Application.Controllers;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Guild.Tests.Controllers;

/// <summary>
/// Enforcement of the two Discord-parity expression bits, and the guarantee that adding them took
/// nothing away from anybody.
/// </summary>
[TestFixture]
public class ExpressionPermissionTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private GuildPermissionService _permissionService = null!;
    private IAmazonS3 _s3 = null!;
    private GuildEmojiController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _permissionService = new GuildPermissionService(
            new FakeDistributedCache(), _context, NullLogger<GuildPermissionService>.Instance);
        _s3 = Substitute.For<IAmazonS3>();
        _controller = new GuildEmojiController(
            _context,
            new GuildEmojiService(_s3),
            _permissionService,
            new AuditLogService(_context),
            new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance),
            new FakeHubContext(),
            new FakeMessageBus())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        _s3.Dispose();
    }

    private static IFormFile MakeFile() =>
        new FormFile(new MemoryStream(Encoding.UTF8.GetBytes("bytes")), 0, 5, "file", "emoji.png")
        {
            Headers = new HeaderDictionary(), ContentType = "image/png",
        };

    private async Task SeedAsync(Permissions permissions, string userId = UserId)
    {
        if (!await _context.Guilds.AnyAsync())
        {
            _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
            {
                Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        _context.Roles.Add(new Role
        {
            Id = $"{RoleId}-{userId}", GuildId = GuildId, Name = "r", Permissions = permissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = $"{MemberId}-{userId}", GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{userId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rm-{userId}", RoleId = $"{RoleId}-{userId}", MemberId = $"{MemberId}-{userId}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
        SetUser(userId);
    }

    private void SetUser(string userId) =>
        _controller.ControllerContext.HttpContext.User = TestPrincipal.Create(userId);

    private async Task<GuildEmoji> SeedEmojiAsync(string createdByUserId, string id = "emoji-1")
    {
        var emoji = GuildEmoji.Create(new CreateGuildEmojiParams
        {
            GuildId = GuildId, Name = $"pepe-{id}", CreatedByUserId = createdByUserId,
        });
        emoji.Id = id;
        _context.GuildEmojis.Add(emoji);
        await _context.SaveChangesAsync();
        return emoji;
    }

    // ══════════════════════════════════════════════════════════════════════════ Create
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Create_IsAllowedByCreateExpressions()
    {
        await SeedAsync(Permissions.CreateExpressions);

        var result = await _controller.CreateEmoji(GuildId, "pepe", false, MakeFile());

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task Create_IsAllowedByManageExpressions()
    {
        await SeedAsync(Permissions.ManageExpressions);

        var result = await _controller.CreateEmoji(GuildId, "pepe", false, MakeFile());

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task Create_StillWorksForTheExistingManageEmojisGrant()
    {
        // The regression that matters on deploy: every emoji moderator in every existing guild holds
        // this bit and no other, and must not need re-granting.
        await SeedAsync(Permissions.ManageEmojis);

        var result = await _controller.CreateEmoji(GuildId, "pepe", false, MakeFile());

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task Create_WithNeitherBit_IsForbidden()
    {
        await SeedAsync(Role.DefaultEveryonePermissions);

        var result = await _controller.CreateEmoji(GuildId, "pepe", false, MakeFile());

        Assert.That(result, Is.InstanceOf<ForbidResult>(),
            "none of the expression bits are in the @everyone defaults");
    }

    // ══════════════════════════════════════════════════════════════════════════ Delete
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Delete_ByAContributor_IsAllowedForTheirOwn()
    {
        await SeedAsync(Permissions.CreateExpressions);
        await SeedEmojiAsync(UserId);

        var result = await _controller.DeleteEmoji(GuildId, "emoji-1");

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(await _context.GuildEmojis.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Delete_ByAContributor_IsForbiddenForSomebodyElses()
    {
        await SeedAsync(Permissions.CreateExpressions);
        await SeedEmojiAsync("someone-else");

        var result = await _controller.DeleteEmoji(GuildId, "emoji-1");

        Assert.That(result, Is.InstanceOf<ForbidResult>());
        Assert.That(await _context.GuildEmojis.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Delete_ByACurator_IsAllowedForAnybodys()
    {
        await SeedAsync(Permissions.ManageExpressions);
        await SeedEmojiAsync("someone-else");

        var result = await _controller.DeleteEmoji(GuildId, "emoji-1");

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task Delete_StillWorksForTheExistingManageEmojisGrant()
    {
        await SeedAsync(Permissions.ManageEmojis);
        await SeedEmojiAsync("someone-else");

        var result = await _controller.DeleteEmoji(GuildId, "emoji-1");

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task Delete_WithNoExpressionBitAtAll_IsForbidden()
    {
        await SeedAsync(Role.DefaultEveryonePermissions);
        await SeedEmojiAsync(UserId);

        var result = await _controller.DeleteEmoji(GuildId, "emoji-1");

        Assert.That(result, Is.InstanceOf<ForbidResult>(),
            "owning the emoji is not on its own a permission to remove it");
    }

    [Test]
    public async Task Delete_OfAnEmojiThatDoesNotExist_IsNotFound()
    {
        await SeedAsync(Permissions.ManageEmojis);

        var result = await _controller.DeleteEmoji(GuildId, "no-such-emoji");

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }
}
