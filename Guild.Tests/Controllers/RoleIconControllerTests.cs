using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using AppEnvironment;
using Guild.Application.Controllers;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Validators;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Guild.Tests.Controllers;

/// <summary>R20: the upload path that makes <c>Role.IconUrl</c> populatable.</summary>
[TestFixture]
public class RoleIconControllerTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string StaffRoleId = "role-staff";
    private const string TargetRoleId = "role-target";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private MfaElevationService _mfa = null!;
    private IAmazonS3 _s3 = null!;
    private RoleIconService _icons = null!;
    private FakeMessageBus _bus = null!;
    private RoleIconController _controller = null!;
    private GeneralConfiguration _originalGeneralConfiguration = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _permissionService = new GuildPermissionService(
            new FakeDistributedCache(), _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _mfa = new MfaElevationService(_context);
        _s3 = Substitute.For<IAmazonS3>();
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>()).Returns("https://storage.test/signed");
        _icons = new RoleIconService(_s3);
        _bus = new FakeMessageBus();
        _controller = new RoleIconController(_context, _icons, _permissionService, _auditLog, _mfa, _bus)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _originalGeneralConfiguration = Env.GeneralConfiguration;
    }

    [TearDown]
    public async Task TearDown()
    {
        Env.GeneralConfiguration = _originalGeneralConfiguration;
        await _context.DisposeAsync();
        _s3.Dispose();
    }

    private void SetUser(string? userId) =>
        _controller.ControllerContext.HttpContext.User =
            userId is null ? TestPrincipal.CreateAnonymous() : TestPrincipal.Create(userId);

    private static IFormFile MakeFile(string content = "bytes", string contentType = "image/png", int? length = null) =>
        new FormFile(new MemoryStream(Encoding.UTF8.GetBytes(content)), 0, length ?? content.Length, "file", "icon.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    /// <summary>Actor at position 5 holding <paramref name="permissions"/>, and a target role at
    /// position 1 they therefore outrank.</summary>
    private async Task<Role> SeedAsync(
        Permissions permissions = Permissions.ManageRoles,
        int actorPosition = 5,
        bool mfaRequired = false,
        bool managedTarget = false,
        string? targetEmoji = null)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild", MfaRequired = mfaRequired,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Roles.Add(new Role
        {
            Id = StaffRoleId, GuildId = GuildId, Name = "staff", Position = actorPosition,
            Permissions = permissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        var target = Role.Create(new CreateRoleParams
        {
            Name = "target", GuildId = GuildId, UnicodeEmoji = targetEmoji,
        });
        target.Id = TargetRoleId;
        target.Position = 1;
        target.IsManaged = managedTarget;
        _context.Roles.Add(target);

        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-staff", RoleId = StaffRoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
        SetUser(UserId);
        return target;
    }

    private Task<Role> ReloadTargetAsync() =>
        _context.Roles.AsNoTracking().FirstAsync(r => r.Id == TargetRoleId);

    // ══════════════════════════════════════════════════════════════════════════ Upload
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Upload_SetsAStableIconUrlAndPutsTheObject()
    {
        await SeedAsync();

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        Assert.That(result, Is.InstanceOf<OkObjectResult>());

        var role = await ReloadTargetAsync();
        Assert.Multiple(() =>
        {
            Assert.That(role.IconUrl, Is.EqualTo(RoleIconService.PublicUrlFor(GuildId, TargetRoleId)));
            Assert.That(role.IconUrl, Does.StartWith("http"),
                "the column is read by clients, federated peers and the bot gateway, so it has to be absolute");
        });

        await _s3.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(r => r.Key == $"role-icons/{GuildId}/{TargetRoleId}"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upload_ClearsAnyUnicodeEmojiBadge()
    {
        await SeedAsync(targetEmoji: "🎉");

        await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        var role = await ReloadTargetAsync();
        Assert.Multiple(() =>
        {
            Assert.That(role.UnicodeEmoji, Is.Null, "Role.SetBadge refuses a role holding both");
            Assert.That(role.IconUrl, Is.Not.Null);
        });
    }

    [Test]
    public async Task Upload_PublishesARoleUpdateSoClientsSeeTheNewBadge()
    {
        await SeedAsync();

        await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        Assert.That(_bus.Published.OfType<Guild.Domain.Events.Role.RoleUpdated>().Count(), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════════ File constraints
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Upload_RejectsAnOversizeFile()
    {
        await SeedAsync();

        var oversize = MakeFile(length: 256 * 1024 + 1);

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, oversize);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That((await ReloadTargetAsync()).IconUrl, Is.Null);
    }

    [Test]
    public async Task Upload_RejectsAnUnsupportedContentType()
    {
        await SeedAsync();

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile(contentType: "image/svg+xml"));

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Upload_RejectsAnEmptyFile()
    {
        await SeedAsync();

        var empty = new FormFile(new MemoryStream(), 0, 0, "file", "empty.png")
        {
            Headers = new HeaderDictionary(), ContentType = "image/png",
        };

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, empty);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Upload_RejectsAUrlTheRoleValidatorWouldNotAccept()
    {
        // The composed URL is validated rather than trusted: it is built from INSTANCE_URL, which an
        // operator writes, and a value with no scheme produces a link no client can resolve.
        await SeedAsync();
        Env.GeneralConfiguration = new GeneralConfiguration { InstanceUrl = "api.venta.gg" };

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        await _s3.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void ComposedUrl_FitsTheValidatorsLengthCap()
    {
        var url = RoleIconService.PublicUrlFor(GuildId, TargetRoleId);

        Assert.That(url, Has.Length.LessThanOrEqualTo(RoleValidator.MaxIconUrlLength));
    }

    // ══════════════════════════════════════════════════════════════════════════ Authorization
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Upload_WithoutManageRoles_IsForbidden()
    {
        await SeedAsync(permissions: Permissions.ViewChannel);

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task Upload_OnARoleTheActorDoesNotOutrank_IsForbidden()
    {
        await SeedAsync(actorPosition: 1);

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task Upload_OnAnIntegrationOwnedRole_IsRefused()
    {
        await SeedAsync(managedTarget: true);

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Upload_Anonymously_IsUnauthorized()
    {
        await SeedAsync();
        SetUser(null);

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task Upload_ForARoleInAnotherGuild_IsNotFound()
    {
        await SeedAsync();

        var result = await _controller.UploadRoleIcon("guild-2", TargetRoleId, MakeFile());

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Upload_WithMfaRequired_AndNoSecondFactor_IsRejectedDistinguishably()
    {
        await SeedAsync(mfaRequired: true);

        var result = await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(((ObjectResult)result).Value),
            Does.Contain("mfaRequired"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Read and delete
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Get_RedirectsToASignedUrlOnceAnIconExists()
    {
        await SeedAsync();
        await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        var result = await _controller.GetRoleIcon(GuildId, TargetRoleId);

        Assert.That(result, Is.InstanceOf<RedirectResult>());
        Assert.That(((RedirectResult)result).Url, Is.EqualTo("https://storage.test/signed"));
    }

    [Test]
    public async Task Get_ForARoleWithNoIcon_IsNotFound()
    {
        await SeedAsync();

        var result = await _controller.GetRoleIcon(GuildId, TargetRoleId);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Delete_ClearsTheColumnAndTheObject()
    {
        await SeedAsync();
        await _controller.UploadRoleIcon(GuildId, TargetRoleId, MakeFile());

        var result = await _controller.DeleteRoleIcon(GuildId, TargetRoleId);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That((await ReloadTargetAsync()).IconUrl, Is.Null);

        await _s3.Received(1).DeleteObjectAsync(
            Arg.Is<DeleteObjectRequest>(r => r.Key == $"role-icons/{GuildId}/{TargetRoleId}"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_WithNoIconSet_IsANoOp()
    {
        await SeedAsync();

        var result = await _controller.DeleteRoleIcon(GuildId, TargetRoleId);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        await _s3.DidNotReceive().DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_WithoutManageRoles_IsForbidden()
    {
        await SeedAsync(permissions: Permissions.ViewChannel);

        var result = await _controller.DeleteRoleIcon(GuildId, TargetRoleId);

        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }
}
