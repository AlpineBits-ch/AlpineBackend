using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints.Persona;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Guild.Tests.Endpoints;

/// <summary>
/// The avatar upload routes, which exist because PersonaDisplayGuard only accepts instance-hosted
/// media: without them a character's picture is unsettable from any client.
/// </summary>
[TestFixture]
public class PersonaAvatarEndpointTests
{
    private const string GuildId = "guild-1";
    private const string UserId = "user-1";
    private const string OtherUserId = "user-2";
    private const string MemberId = "member-1";
    private const string RoleId = "role-1";
    private const string PersonaId = "pers-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissions = null!;
    private PersonaService _personas = null!;
    private PersonaPageService _pages = null!;
    private AuditLogService _auditLog = null!;
    private RoleplayRealtimeService _realtime = null!;
    private IAmazonS3 _s3 = null!;
    private PersonaAvatarService _avatars = null!;
    private PersonaAvatarEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _personas = new PersonaService(_cache, _context);
        _pages = new PersonaPageService(_context);
        _auditLog = new AuditLogService(_context);
        _realtime = RoleplayTestFactory.CreateRealtime(_context, _permissions, _personas, new FakeHubContext());
        _s3 = Substitute.For<IAmazonS3>();
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>()).Returns("https://storage.test/signed");
        _avatars = new PersonaAvatarService(_s3);
        _endpoint = new PersonaAvatarEndpoint();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        _s3.Dispose();
    }

    private static IFormFile MakeFile(
        string content = "bytes", string contentType = "image/png", int? length = null) =>
        new FormFile(new MemoryStream(Encoding.UTF8.GetBytes(content)), 0, length ?? content.Length, "file", "avatar.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    private async Task SeedGuildAsync(ModulePermissions module = ModulePermissions.UsePersonas)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner-1", Name = "Blackwater", Features = GuildFeatures.Personas,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "Players",
            Permissions = Permissions.ViewChannel | Permissions.SendMessages,
            ModulePermissions = module,
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

    private async Task SeedPersonaAsync(
        PersonaScope scope = PersonaScope.User, bool adopted = true, string? avatarUrl = null,
        PersonaApprovalState approval = PersonaApprovalState.Approved)
    {
        _context.Set<Persona>().Add(new Persona
        {
            Id = PersonaId,
            Scope = scope,
            OwnerUserId = scope == PersonaScope.User ? UserId : null,
            OwnerGuildId = scope == PersonaScope.Guild ? GuildId : null,
            Name = "Mayor Cogsgrove",
            AvatarUrl = avatarUrl,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        if (adopted)
        {
            _context.Set<PersonaGuildProfile>().Add(new PersonaGuildProfile
            {
                Id = "pgpf-1", PersonaId = PersonaId, GuildId = GuildId, ApprovalState = approval,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    private Task<IResult> UploadAsync(IFormFile file, string? actor = null) =>
        _endpoint.UploadAsync(PersonaId, file, _permissions, _personas, _avatars, _auditLog,
            _realtime, _context, TestPrincipal.Create(actor ?? UserId));

    private Task<IResult> DeleteAsync(string? actor = null) =>
        _endpoint.DeleteAsync(PersonaId, _permissions, _personas, _avatars, _auditLog,
            _realtime, _context, TestPrincipal.Create(actor ?? UserId));

    private Task<IResult> UploadProfileAsync(IFormFile file, string? actor = null) =>
        _endpoint.UploadProfileAsync(GuildId, PersonaId, file, _permissions, _personas, _avatars,
            _pages, _realtime, _context, TestPrincipal.Create(actor ?? UserId));

    private Task<IResult> DeleteProfileAsync(string? actor = null) =>
        _endpoint.DeleteProfileAsync(GuildId, PersonaId, _permissions, _personas, _avatars,
            _pages, _realtime, _context, TestPrincipal.Create(actor ?? UserId));

    private async Task<Persona> ReloadPersonaAsync() => (await _context.Set<Persona>().FindAsync(PersonaId))!;

    private Task<PersonaGuildProfile> ReloadProfileAsync() =>
        _context.Set<PersonaGuildProfile>().FirstAsync(p => p.PersonaId == PersonaId && p.GuildId == GuildId);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The character's own picture

    [Test]
    public async Task Upload_WritesAUrlTheDisplayGuardAcceptsAndPutsTheObject()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        var result = await UploadAsync(MakeFile());

        Assert.That(result, Is.InstanceOf<Ok<PersonaDto>>());

        var persona = await ReloadPersonaAsync();
        Assert.Multiple(() =>
        {
            Assert.That(PersonaDisplayGuard.ValidateAvatar(persona.AvatarUrl), Is.Null,
                "the upload composes the only value the persona routes will accept, so the guard has to accept it too");
            Assert.That(persona.AvatarUrl, Does.StartWith("http"));
            Assert.That(persona.AvatarUrl!, Has.Length.LessThanOrEqualTo(PersonaLimits.MaxAvatarUrlLength));
        });

        await _s3.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(r => r.Key == $"persona-avatars/{PersonaId}/global"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upload_ComposesAUrlThatReachesTheGatewayPrefix()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        await UploadAsync(MakeFile());

        // The gateway serves this service under /api/v1/guild; a URL built from the route as the
        // service sees it 404s for every client.
        Assert.That((await ReloadPersonaAsync()).AvatarUrl,
            Does.Contain($"/api/v1/guild/personas/{PersonaId}/avatar"));
    }

    [Test]
    public async Task Upload_RewritesTheUrlSoACacheSeesTheNewPicture()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        await UploadAsync(MakeFile());
        var first = (await ReloadPersonaAsync()).AvatarUrl;

        // The object key is stable, so the URL is the only thing that can tell a cache, a broken
        // image record or an already-rendered img tag that the picture changed. Well clear of the
        // Windows clock's granularity.
        await Task.Delay(50);

        await UploadAsync(MakeFile());

        Assert.That((await ReloadPersonaAsync()).AvatarUrl, Is.Not.EqualTo(first));
    }

    [Test]
    public async Task Upload_StoresTheAllowlistedContentTypeRatherThanTheCallers()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        await UploadAsync(MakeFile(contentType: "IMAGE/PNG"));

        await _s3.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(r => r.ContentType == "image/png"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upload_RejectsAnUnsupportedContentType()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        var result = await UploadAsync(MakeFile(contentType: "image/svg+xml"));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That((await ReloadPersonaAsync()).AvatarUrl, Is.Null);
        await _s3.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upload_RejectsAnOversizeFile()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        var result = await UploadAsync(MakeFile(length: 2 * 1024 * 1024 + 1));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That((await ReloadPersonaAsync()).AvatarUrl, Is.Null);
    }

    [Test]
    public async Task Upload_RejectsAnEmptyFile()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        var empty = new FormFile(new MemoryStream(), 0, 0, "file", "empty.png")
        {
            Headers = new HeaderDictionary(), ContentType = "image/png",
        };

        Assert.That(await UploadAsync(empty), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Upload_OnSomebodyElsesCharacter_IsNotFound()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        var result = await UploadAsync(MakeFile(), actor: OtherUserId);

        Assert.That(result, Is.InstanceOf<NotFound>(),
            "the persona patch answers a non-owner the same way, and saying Forbid would confirm the character exists");
        await _s3.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upload_OnAGuildOwnedCharacter_NeedsManageAnyPersona()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync(PersonaScope.Guild);

        Assert.That(await UploadAsync(MakeFile()), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Upload_OnAGuildOwnedCharacter_WithManageAnyPersona_IsAllowed()
    {
        await SeedGuildAsync(ModulePermissions.UsePersonas | ModulePermissions.ManageAnyPersona);
        await SeedPersonaAsync(PersonaScope.Guild);

        Assert.That(await UploadAsync(MakeFile()), Is.InstanceOf<Ok<PersonaDto>>());
        Assert.That((await ReloadPersonaAsync()).AvatarUrl, Is.Not.Null);
    }

    [Test]
    public async Task Delete_ClearsTheColumnAndTheObject()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync(avatarUrl: "https://cdn.test/old.png");

        var result = await DeleteAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That((await ReloadPersonaAsync()).AvatarUrl, Is.Null);

        await _s3.Received(1).DeleteObjectAsync(
            Arg.Is<DeleteObjectRequest>(r => r.Key == $"persona-avatars/{PersonaId}/global"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_WithNoAvatarSet_IsANoOp()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        Assert.That(await DeleteAsync(), Is.InstanceOf<NoContent>());
        await _s3.DidNotReceive().DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Get_RedirectsToASignedUrlOnceAnAvatarExists()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();
        await UploadAsync(MakeFile());

        // The route reads the row untracked, and in production Wolverine's middleware has committed
        // the upload by the time anything fetches the picture.
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetAsync(PersonaId, _avatars, _context);

        Assert.That(result, Is.InstanceOf<RedirectHttpResult>());
        Assert.That(((RedirectHttpResult)result).Url, Is.EqualTo("https://storage.test/signed"));
    }

    [Test]
    public async Task Get_ForACharacterWithNoAvatar_IsNotFound()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        Assert.That(await _endpoint.GetAsync(PersonaId, _avatars, _context), Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // This guild's override

    [Test]
    public async Task UploadProfile_SetsTheOverrideAndLeavesTheCharacterRowAlone()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        var result = await UploadProfileAsync(MakeFile());

        Assert.That(result, Is.InstanceOf<Ok<PersonaGuildProfileDto>>());
        Assert.Multiple(async () =>
        {
            Assert.That((await ReloadProfileAsync()).AvatarUrl, Is.Not.Null);
            Assert.That((await ReloadPersonaAsync()).AvatarUrl, Is.Null,
                "an override is this guild's opinion of the character, not an edit to the character");
        });

        await _s3.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(r => r.Key == $"persona-avatars/{PersonaId}/{GuildId}"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UploadProfile_ForACharacterThisGuildHasNotAdopted_IsNotFound()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync(adopted: false);

        Assert.That(await UploadProfileAsync(MakeFile()), Is.InstanceOf<NotFound>());
        await _s3.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UploadProfile_LeavesTheApprovalStateAlone()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync(approval: PersonaApprovalState.Approved);

        await UploadProfileAsync(MakeFile());

        Assert.That((await ReloadProfileAsync()).ApprovalState, Is.EqualTo(PersonaApprovalState.Approved),
            "the profile PUT does not re-open approval on an edit either");
    }

    [Test]
    public async Task UploadProfile_ByANonMember_IsForbidden()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();

        Assert.That(await UploadProfileAsync(MakeFile(), actor: OtherUserId), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteProfile_ClearsTheOverrideAndTheObject()
    {
        await SeedGuildAsync();
        await SeedPersonaAsync();
        await UploadProfileAsync(MakeFile());

        var result = await DeleteProfileAsync();

        Assert.That(result, Is.InstanceOf<Ok<PersonaGuildProfileDto>>());
        Assert.That((await ReloadProfileAsync()).AvatarUrl, Is.Null);

        await _s3.Received(1).DeleteObjectAsync(
            Arg.Is<DeleteObjectRequest>(r => r.Key == $"persona-avatars/{PersonaId}/{GuildId}"),
            Arg.Any<CancellationToken>());
    }
}
