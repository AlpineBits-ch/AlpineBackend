using Amazon.S3;
using Guild.Application.Controllers;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Guild.Tests.Controllers;

/// <summary>
/// Covers GuildEmojiController: list/create/delete, ManageEmojis permission gating, and the S3
/// upload/delete calls (via a NSubstitute IAmazonS3, same rationale as GuildEmojiServiceTests).
/// </summary>
[TestFixture]
public class GuildEmojiControllerTests
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
    private GuildHydrateService _hydrateService = null!;
    private FakeHubContext _hub = null!;
    private IAmazonS3 _s3 = null!;
    private GuildEmojiService _emojiService = null!;
    private GuildEmojiController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = PermissionTestFactory.Create(_cache, _context);
        _auditLog = new AuditLogService(_context);
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _hub = new FakeHubContext();
        _s3 = Substitute.For<IAmazonS3>();
        _emojiService = new GuildEmojiService(_s3);
        _controller = new GuildEmojiController(_context, _emojiService, _permissionService, _auditLog, _hydrateService, _hub, new FakeMessageBus())
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

    private void SetUser(string? userId) =>
        _controller.ControllerContext.HttpContext.User = userId is null ? TestPrincipal.CreateAnonymous() : TestPrincipal.Create(userId);

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedMemberWithPermission(Permissions permission)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "r", Permissions = permission, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    private static IFormFile MakeFile(string content = "bytes") =>
        new FormFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), 0, content.Length, "file", "emoji.png")
        {
            Headers = new HeaderDictionary(), ContentType = "image/png",
        };

    // ══════════════════════════════════════════════════════════════════════ GetEmojis
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetEmojis_Unauthenticated_ReturnsUnauthorized()
    {
        SetUser(null);
        var result = await _controller.GetEmojis(GuildId);
        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task GetEmojis_LacksViewChannel_ReturnsForbid()
    {
        SetUser(UserId);
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _controller.GetEmojis(GuildId);
        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task GetEmojis_Valid_ReturnsEmojisWithPresignedUrls()
    {
        SetUser(UserId);
        await SeedMemberWithPermission(Permissions.ViewChannel);
        _context.GuildEmojis.Add(GuildEmoji.Create(new CreateGuildEmojiParams { GuildId = GuildId, Name = "pepe", CreatedByUserId = UserId, Animated = false }));
        await _context.SaveChangesAsync();
        _s3.GetPreSignedURL(Arg.Any<Amazon.S3.Model.GetPreSignedUrlRequest>()).Returns("https://cdn/pepe.png");

        var result = await _controller.GetEmojis(GuildId) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var list = ((IEnumerable<Guild.Application.Dtos.Response.GuildEmojiDto>)result!.Value!).ToList();
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].ImageUrl, Is.EqualTo("https://cdn/pepe.png"));
    }

    // ══════════════════════════════════════════════════════════════════════ CreateEmoji
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateEmoji_Unauthenticated_ReturnsUnauthorized()
    {
        SetUser(null);
        var result = await _controller.CreateEmoji(GuildId, "pepe", false, MakeFile());
        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task CreateEmoji_LacksManageEmojis_ReturnsForbid()
    {
        SetUser(UserId);
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _controller.CreateEmoji(GuildId, "pepe", false, MakeFile());
        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task CreateEmoji_MissingNameOrEmptyFile_ReturnsBadRequest()
    {
        SetUser(UserId);
        await SeedMemberWithPermission(Permissions.ManageEmojis);

        var result1 = await _controller.CreateEmoji(GuildId, "  ", false, MakeFile());
        var result2 = await _controller.CreateEmoji(GuildId, "pepe", false,
            new FormFile(new MemoryStream(), 0, 0, "file", "empty.png") { Headers = new HeaderDictionary(), ContentType = "image/png" });

        Assert.Multiple(() =>
        {
            Assert.That(result1, Is.InstanceOf<BadRequestResult>());
            Assert.That(result2, Is.InstanceOf<BadRequestResult>());
        });
    }

    [Test]
    public async Task CreateEmoji_NameAlreadyTaken_ReturnsConflict_CaseInsensitive()
    {
        SetUser(UserId);
        await SeedMemberWithPermission(Permissions.ManageEmojis);
        _context.GuildEmojis.Add(GuildEmoji.Create(new CreateGuildEmojiParams { GuildId = GuildId, Name = "Pepe", CreatedByUserId = UserId, Animated = false }));
        await _context.SaveChangesAsync();

        var result = await _controller.CreateEmoji(GuildId, "pepe", false, MakeFile());

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
    }

    [Test]
    public async Task CreateEmoji_Valid_UploadsToS3_PersistsRow_AndCommitsItself()
    {
        SetUser(UserId);
        await SeedMemberWithPermission(Permissions.ManageEmojis);

        var result = await _controller.CreateEmoji(GuildId, "pepe", true, MakeFile());

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        await _s3.Received(1).PutObjectAsync(Arg.Any<Amazon.S3.Model.PutObjectRequest>(), Arg.Any<CancellationToken>());

        // No manual SaveChangesAsync() call from the test - this endpoint is an MVC controller
        // (not Wolverine-dispatched), so it must have committed on its own.
        var emoji = _context.GuildEmojis.FirstOrDefault(e => e.GuildId == GuildId && e.Name == "pepe");
        Assert.That(emoji, Is.Not.Null);
        Assert.That(emoji!.Animated, Is.True);
    }

    [Test]
    public async Task CreateEmoji_Valid_WritesAuditLogEntry()
    {
        SetUser(UserId);
        await SeedMemberWithPermission(Permissions.ManageEmojis);

        await _controller.CreateEmoji(GuildId, "pepe", false, MakeFile());

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.GuildId == GuildId).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].ActionType, Is.EqualTo(AuditActionType.EmojiCreated));
    }

    // ══════════════════════════════════════════════════════════════════════ DeleteEmoji
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteEmoji_Unauthenticated_ReturnsUnauthorized()
    {
        SetUser(null);
        var result = await _controller.DeleteEmoji(GuildId, "emoji-1");
        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task DeleteEmoji_LacksManageEmojis_ReturnsForbid()
    {
        SetUser(UserId);
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _controller.DeleteEmoji(GuildId, "emoji-1");
        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task DeleteEmoji_DoesNotExist_ReturnsNotFound()
    {
        SetUser(UserId);
        await SeedMemberWithPermission(Permissions.ManageEmojis);

        var result = await _controller.DeleteEmoji(GuildId, "nonexistent");
        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task DeleteEmoji_Valid_RemovesRow_AndDeletesFromS3()
    {
        SetUser(UserId);
        await SeedMemberWithPermission(Permissions.ManageEmojis);
        var emoji = GuildEmoji.Create(new CreateGuildEmojiParams { GuildId = GuildId, Name = "pepe", CreatedByUserId = UserId, Animated = false });
        _context.GuildEmojis.Add(emoji);
        await _context.SaveChangesAsync();

        var result = await _controller.DeleteEmoji(GuildId, emoji.Id);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(_context.GuildEmojis.Find(emoji.Id), Is.Null);
        await _s3.Received(1).DeleteObjectAsync(Arg.Is<Amazon.S3.Model.DeleteObjectRequest>(r => r.Key == $"emojis/{GuildId}/{emoji.Id}"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteEmoji_Valid_WritesAuditLogEntry()
    {
        SetUser(UserId);
        await SeedMemberWithPermission(Permissions.ManageEmojis);
        var emoji = GuildEmoji.Create(new CreateGuildEmojiParams { GuildId = GuildId, Name = "pepe", CreatedByUserId = UserId, Animated = false });
        _context.GuildEmojis.Add(emoji);
        await _context.SaveChangesAsync();

        await _controller.DeleteEmoji(GuildId, emoji.Id);

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.GuildId == GuildId && e.ActionType == AuditActionType.EmojiDeleted).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }
}
