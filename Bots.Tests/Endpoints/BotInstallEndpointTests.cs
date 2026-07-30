using System.Security.Claims;
using Bots.Application.Dtos.Request;
using Bots.Application.Dtos.Response;
using Bots.Application.Endpoints;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts;
using Guild.Contracts.Bus.Commands;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints;

[TestFixture]
public class BotInstallEndpointTests
{
    private TestBotsContext _context = null!;
    private FakeInstallMessageBus _bus = null!;
    private BotInstallEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestBotsContext(Guid.NewGuid().ToString());
        _bus = new FakeInstallMessageBus();
        _endpoint = new BotInstallEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private async Task<BotApplication> AddEnabledApplicationAsync(string ownerUserId = "usr_owner", string botUserId = "usr_bot1")
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = ownerUserId, BotUserId = botUserId, Name = "Test Bot", IsEnabled = true };
        _context.BotApplications.Add(app);
        await _context.SaveChangesAsync();
        return app;
    }

    // ── GetManageableGuildsAsync ─────────────────────────────────────────────

    [Test]
    public async Task GetManageableGuilds_Authenticated_ReturnsGuildsFromBus()
    {
        _bus.ManageableGuildsResponse = new ListManageableGuildsForUserResponse
        {
            Guilds = [new ManageableGuildSummary { Id = "gld_1", Name = "My Guild" }]
        };

        var result = await _endpoint.GetManageableGuildsAsync(MakeUser("usr_1"), _bus);

        var ok = (Ok<List<ManageableGuildSummary>>)result;
        Assert.That(ok.Value!.Single().Id, Is.EqualTo("gld_1"));
    }

    [Test]
    public async Task GetManageableGuilds_Unauthenticated_ReturnsUnauthorized()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _endpoint.GetManageableGuildsAsync(anonymous, _bus);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    // ── GetAuthorizeInfoAsync ────────────────────────────────────────────────

    [Test]
    public async Task GetAuthorizeInfo_KnownEnabledApp_ReturnsConsentInfo()
    {
        var app = await AddEnabledApplicationAsync(botUserId: "usr_bot1");
        _bus.ResolvedPermissionsResponse = new ResolveInstallablePermissionsResponse { HasManageGuild = true, ClampedPermissions = 1024 };

        var result = await _endpoint.GetAuthorizeInfoAsync("usr_bot1", 2048, "gld_1", MakeUser("usr_installer"), _context, _bus);

        var ok = (Ok<AuthorizeConsentDto>)result;
        Assert.That(ok.Value!.GrantablePermissions, Is.EqualTo((ulong)1024));
    }

    [Test]
    public async Task GetAuthorizeInfo_UnknownClientId_ReturnsNotFound()
    {
        var result = await _endpoint.GetAuthorizeInfoAsync("usr_unknown", 1024, "gld_1", MakeUser("usr_installer"), _context, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetAuthorizeInfo_DisabledApp_ReturnsNotFound()
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = "usr_owner", BotUserId = "usr_bot1", Name = "Bot", IsEnabled = false };
        _context.BotApplications.Add(app);
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetAuthorizeInfoAsync("usr_bot1", 1024, "gld_1", MakeUser("usr_installer"), _context, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetAuthorizeInfo_InstallerLacksManageGuild_ReturnsForbid()
    {
        await AddEnabledApplicationAsync(botUserId: "usr_bot1");
        _bus.ResolvedPermissionsResponse = new ResolveInstallablePermissionsResponse { HasManageGuild = false };

        var result = await _endpoint.GetAuthorizeInfoAsync("usr_bot1", 1024, "gld_1", MakeUser("usr_installer"), _context, _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // ── ApproveInstallAsync ──────────────────────────────────────────────────

    [Test]
    public async Task ApproveInstall_NewInstallation_CreatesBotInstallationRow()
    {
        var app = await AddEnabledApplicationAsync(botUserId: "usr_bot1");
        _bus.ResolvedPermissionsResponse = new ResolveInstallablePermissionsResponse { HasManageGuild = true, ClampedPermissions = 512 };
        _bus.CreateMemberResponse = new CreateBotGuildMemberResponse { GuildMemberId = "gm_1" };

        var result = await _endpoint.ApproveInstallAsync(
            new ApproveInstallDto { ClientId = "usr_bot1", GuildId = "gld_1", Permissions = 1024 },
            MakeUser("usr_installer"), _context, _bus);
        await _context.SaveChangesAsync();

        var installation = _context.BotInstallations.Single();
        Assert.That(installation.BotApplicationId, Is.EqualTo(app.Id));
        Assert.That(installation.GuildMemberId, Is.EqualTo("gm_1"));
        Assert.That(installation.GrantedPermissions, Is.EqualTo((ulong)512));
    }

    [Test]
    public async Task ApproveInstall_AlreadyInstalled_UpdatesGrantedPermissionsInsteadOfDuplicating()
    {
        var app = await AddEnabledApplicationAsync(botUserId: "usr_bot1");
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = "gld_1",
            InstalledByUserId = "usr_installer", GuildMemberId = "gm_1", GrantedPermissions = 8,
            InstalledAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
        _bus.ResolvedPermissionsResponse = new ResolveInstallablePermissionsResponse { HasManageGuild = true, ClampedPermissions = 999 };

        await _endpoint.ApproveInstallAsync(
            new ApproveInstallDto { ClientId = "usr_bot1", GuildId = "gld_1", Permissions = 1024 },
            MakeUser("usr_installer"), _context, _bus);
        await _context.SaveChangesAsync();

        Assert.That(_context.BotInstallations.Count(), Is.EqualTo(1));
        Assert.That(_context.BotInstallations.Single().GrantedPermissions, Is.EqualTo((ulong)999));
    }

    [Test]
    public async Task ApproveInstall_WithRedirectUri_RedirectsWithGuildIdAndPermissions()
    {
        await AddEnabledApplicationAsync(botUserId: "usr_bot1");
        _bus.ResolvedPermissionsResponse = new ResolveInstallablePermissionsResponse { HasManageGuild = true, ClampedPermissions = 64 };

        var result = await _endpoint.ApproveInstallAsync(
            new ApproveInstallDto { ClientId = "usr_bot1", GuildId = "gld_1", Permissions = 1024, RedirectUri = "https://client.example.com/cb" },
            MakeUser("usr_installer"), _context, _bus);

        var redirect = (RedirectHttpResult)result;
        Assert.That(redirect.Url, Is.EqualTo("https://client.example.com/cb?guild_id=gld_1&permissions=64"));
    }

    [Test]
    public async Task ApproveInstall_NoManageGuild_ReturnsForbidAndCreatesNoInstallation()
    {
        await AddEnabledApplicationAsync(botUserId: "usr_bot1");
        _bus.ResolvedPermissionsResponse = new ResolveInstallablePermissionsResponse { HasManageGuild = false };

        var result = await _endpoint.ApproveInstallAsync(
            new ApproveInstallDto { ClientId = "usr_bot1", GuildId = "gld_1", Permissions = 1024 },
            MakeUser("usr_installer"), _context, _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(_context.BotInstallations.Any(), Is.False);
    }

    // ── GetInstallationsAsync ────────────────────────────────────────────────

    [Test]
    public async Task GetInstallations_Owner_ReturnsInstallations()
    {
        var app = await AddEnabledApplicationAsync("usr_owner", "usr_bot1");
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = "gld_1",
            InstalledByUserId = "usr_installer", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetInstallationsAsync(app.Id, MakeUser("usr_owner"), _context);

        var ok = (Ok<List<BotInstallationDto>>)result;
        Assert.That(ok.Value!.Single().GuildId, Is.EqualTo("gld_1"));
    }

    [Test]
    public async Task GetInstallations_NotOwner_ReturnsForbid()
    {
        var app = await AddEnabledApplicationAsync("usr_owner", "usr_bot1");

        var result = await _endpoint.GetInstallationsAsync(app.Id, MakeUser("usr_intruder"), _context);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // ── UninstallAsync ───────────────────────────────────────────────────────

    [Test]
    public async Task Uninstall_ByOwner_RemovesInstallationWithoutPermissionCheck()
    {
        var app = await AddEnabledApplicationAsync("usr_owner", "usr_bot1");
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = "gld_1",
            InstalledByUserId = "usr_owner", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await _endpoint.UninstallAsync(app.Id, "gld_1", MakeUser("usr_owner"), _context, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(_context.BotInstallations.Any(), Is.False);
        Assert.That(_bus.Invoked.Any(m => m is HasUserPermissionToGuildRequest), Is.False,
            "Owner uninstall must skip the ManageGuild bus round-trip entirely");
    }

    [Test]
    public async Task Uninstall_ByNonOwnerWithManageGuild_RemovesInstallation()
    {
        var app = await AddEnabledApplicationAsync("usr_owner", "usr_bot1");
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = "gld_1",
            InstalledByUserId = "usr_owner", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
        _bus.HasPermissionResponse = new HasUserPermissionToGuildResponse { IsAllowed = true, Permission = ExternalPermission.ManageGuild };

        var result = await _endpoint.UninstallAsync(app.Id, "gld_1", MakeUser("usr_moderator"), _context, _bus);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(_context.BotInstallations.Any(), Is.False);
    }

    [Test]
    public async Task Uninstall_ByNonOwnerWithoutManageGuild_ReturnsForbidAndKeepsInstallation()
    {
        var app = await AddEnabledApplicationAsync("usr_owner", "usr_bot1");
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = "gld_1",
            InstalledByUserId = "usr_owner", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
        _bus.HasPermissionResponse = new HasUserPermissionToGuildResponse { IsAllowed = false, Permission = ExternalPermission.ManageGuild };

        var result = await _endpoint.UninstallAsync(app.Id, "gld_1", MakeUser("usr_intruder"), _context, _bus);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(_context.BotInstallations.Any(), Is.True);
    }

    [Test]
    public async Task Uninstall_UnknownApplication_ReturnsNotFound()
    {
        var result = await _endpoint.UninstallAsync("boap_missing", "gld_1", MakeUser("usr_owner"), _context, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }
}
