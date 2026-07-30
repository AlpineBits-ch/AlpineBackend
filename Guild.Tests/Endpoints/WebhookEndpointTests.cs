using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers WebhookEndpoint: Get/Create/Delete (ManageWebhooks-gated - unlike the WolverineHttp
/// endpoints elsewhere, these use plain Mvc Http* attributes and take ClaimsPrincipal directly
/// into GuildPermissionService's overload, so an anonymous caller resolves to Forbid rather than
/// a separate Unauthorized branch), RegenerateToken, and ExecuteWebhook - which is anonymous by
/// design and authenticated solely by the token in its path, so the token comparison and the
/// wrong-token-looks-like-missing behaviour are the parts worth covering there.
/// </summary>
[TestFixture]
public class WebhookEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private FakeInvokingMessageBus _bus = null!;
    private WebhookEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _bus = new FakeInvokingMessageBus();
        _endpoint = new WebhookEndpoint();
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
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageWebhooks, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════
    // GetWebhooksByGuildAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetWebhooks_Unauthenticated_ReturnsForbid()
    {
        var result = await _endpoint.GetWebhooksByGuildAsync(GuildId, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetWebhooks_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetWebhooksByGuildAsync(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetWebhooks_Valid_ReturnsWebhooks()
    {
        await SeedManagerMember();
        _context.WebhookConfigs.Add(new WebhookConfig { Id = "wh-1", GuildId = GuildId, ChannelId = "chan-1", CreatedBy = UserId, Token = Token, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetWebhooksByGuildAsync(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        var ok = result as Ok<List<Guild.Application.Dtos.Response.WebhookWithTokenDto>>;
        Assert.That(ok!.Value, Has.Count.EqualTo(1));
        Assert.That(ok.Value![0].Url, Does.Contain($"/api/webhooks/wh-1/{Token}"),
            "the executable URL is the deliverable - a management list without it is unusable");
    }

    // ══════════════════════════════════════════════════════════════════════
    // CreateWebhookAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateWebhook_LacksManageChannel_ReturnsForbid()
    {
        var result = await _endpoint.CreateWebhookAsync(GuildId, new CreateWebhookDto { Name = "Hook", ChannelId = "chan-1" }, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateWebhook_Valid_PersistsWebhook()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateWebhookAsync(GuildId, new CreateWebhookDto { Name = "My Hook", ChannelId = "chan-1" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.WebhookWithTokenDto>;
        Assert.That(ok, Is.Not.Null);
        var created = await _context.WebhookConfigs.AsNoTracking().FirstAsync(w => w.Id == ok!.Value!.Id);
        Assert.Multiple(() =>
        {
            Assert.That(created.Name, Is.EqualTo("My Hook"));
            Assert.That(created.CreatedBy, Is.EqualTo(UserId));
            Assert.That(created.Token, Is.Not.Empty, "a webhook created without a token would be unusable");
            Assert.That(ok.Value!.Token, Is.EqualTo(created.Token));
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // DeleteWebhookAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteWebhook_LacksManageChannel_ReturnsForbid()
    {
        var result = await _endpoint.DeleteWebhookAsync("wh-1", GuildId, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteWebhook_DoesNotExist_ReturnsNotFound()
    {
        await SeedManagerMember();
        var result = await _endpoint.DeleteWebhookAsync("nonexistent", GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteWebhook_Valid_RemovesWebhook()
    {
        await SeedManagerMember();
        _context.WebhookConfigs.Add(new WebhookConfig { Id = "wh-1", GuildId = GuildId, ChannelId = "chan-1", CreatedBy = UserId, Token = Token, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.DeleteWebhookAsync("wh-1", GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.WebhookConfigDto>>());
        Assert.That(await _context.WebhookConfigs.AsNoTracking().AnyAsync(w => w.Id == "wh-1"), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ExecuteWebhook
    // ══════════════════════════════════════════════════════════════════════

    private const string Token = "test-token-abcdefghijklmnopqrstuvwxyz";

    private async Task<WebhookConfig> SeedExecutableWebhook(string? avatarUrl = null, string name = "Captain Hook")
    {
        var webhook = new WebhookConfig
        {
            Id = "wh-1", GuildId = GuildId, ChannelId = "chan-1", CreatedBy = UserId, Name = name,
            AvatarUrl = avatarUrl, Token = Token,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        _context.WebhookConfigs.Add(webhook);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return webhook;
    }

    [Test]
    public async Task ExecuteWebhook_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.ExecuteWebhook("nonexistent", Token, new WebhookRequestDto { UserName = "bot", Content = "hi" }, _context, _bus);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task ExecuteWebhook_WrongToken_ReturnsNotFoundNotForbidden()
    {
        await SeedExecutableWebhook();

        var result = await _endpoint.ExecuteWebhook("wh-1", "not-the-token", new WebhookRequestDto { Content = "hi" }, _context, _bus);

        Assert.That(result, Is.InstanceOf<NotFound>(),
            "a wrong token must be indistinguishable from a missing webhook, or the endpoint enumerates ids");
        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty);
    }

    [Test]
    public async Task ExecuteWebhook_ValidToken_SendsCreateMessageCommand()
    {
        await SeedExecutableWebhook();

        var result = await _endpoint.ExecuteWebhook("wh-1", Token, new WebhookRequestDto { UserName = "deploy-bot", Content = "hi there" }, _context, _bus);

        Assert.That(result, Is.InstanceOf<NoContent>());
        var sent = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(sent.ChannelId, Is.EqualTo("chan-1"));
            Assert.That(sent.AuthorIdType, Is.EqualTo(AuthorIdType.Webhook));
            Assert.That(sent.AuthorId, Is.EqualTo("wh-1"),
                "the author must be the webhook entity, not the caller-supplied display string");
            Assert.That(sent.AuthorDisplayName, Is.EqualTo("deploy-bot"));
        });
    }

    [Test]
    public async Task ExecuteWebhook_NoUsernameSupplied_FallsBackToConfiguredNameAndAvatar()
    {
        await SeedExecutableWebhook(avatarUrl: "https://example.test/hook.png", name: "Configured Hook");

        await _endpoint.ExecuteWebhook("wh-1", Token, new WebhookRequestDto { Content = "hi" }, _context, _bus);

        var sent = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(sent.AuthorDisplayName, Is.EqualTo("Configured Hook"));
            Assert.That(sent.AuthorAvatarUrl, Is.EqualTo("https://example.test/hook.png"));
        });
    }

    [Test]
    public async Task ExecuteWebhook_PerCallOverrides_BeatTheConfiguredDefaults()
    {
        await SeedExecutableWebhook(avatarUrl: "https://example.test/default.png", name: "Configured Hook");

        await _endpoint.ExecuteWebhook("wh-1", Token,
            new WebhookRequestDto { Content = "hi", UserName = "override", AvatarUrl = "https://example.test/override.png" },
            _context, _bus);

        var sent = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(sent.AuthorDisplayName, Is.EqualTo("override"));
            Assert.That(sent.AuthorAvatarUrl, Is.EqualTo("https://example.test/override.png"));
        });
    }

    [Test]
    public async Task ExecuteWebhook_EmbedsArePersistedAsJson_NotDropped()
    {
        await SeedExecutableWebhook();

        await _endpoint.ExecuteWebhook("wh-1", Token, new WebhookRequestDto
        {
            Content = "build failed",
            Embeds =
            [
                new WebhookEmbedDto
                {
                    Title = "Pipeline #42", Description = "3 tests failed", Url = "https://ci.test/42",
                    Fields = [new WebhookEmbedFieldDto { Name = "Branch", Value = "main", Inline = true }],
                },
            ],
        }, _context, _bus);

        var sent = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.That(sent.EmbedsJson, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(sent.EmbedsJson, Does.Contain("Pipeline #42"));
            Assert.That(sent.EmbedsJson, Does.Contain("Branch"));
        });
    }

    [Test]
    public async Task ExecuteWebhook_EmbedsWithNoContent_FlattenToReadableText()
    {
        await SeedExecutableWebhook();

        await _endpoint.ExecuteWebhook("wh-1", Token, new WebhookRequestDto
        {
            Embeds = [new WebhookEmbedDto { Title = "Alert", Description = "Disk at 95%" }],
        }, _context, _bus);

        var sent = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        var content = System.Text.Encoding.UTF8.GetString(sent.Content);
        Assert.Multiple(() =>
        {
            Assert.That(content, Does.Contain("Alert"), "an embed-only alert must not post as a blank message");
            Assert.That(content, Does.Contain("Disk at 95%"));
        });
    }

    [Test]
    public async Task ExecuteWebhook_NeitherContentNorEmbeds_ReturnsBadRequest()
    {
        await SeedExecutableWebhook();

        var result = await _endpoint.ExecuteWebhook("wh-1", Token, new WebhookRequestDto(), _context, _bus);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════
    // RegenerateTokenAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RegenerateToken_LacksManageWebhooks_ReturnsForbid()
    {
        await SeedExecutableWebhook();

        var result = await _endpoint.RegenerateTokenAsync(GuildId, "wh-1", _permissionService, _context, TestPrincipal.CreateAnonymous());

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task RegenerateToken_Valid_ReplacesTokenAndInvalidatesTheOldUrl()
    {
        await SeedManagerMember();
        await SeedExecutableWebhook();

        var result = await _endpoint.RegenerateTokenAsync(GuildId, "wh-1", _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var dto = result as Ok<Guild.Application.Dtos.Response.WebhookWithTokenDto>;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Value!.Token, Is.Not.EqualTo(Token));

        _context.ChangeTracker.Clear();
        var executeWithOldToken = await _endpoint.ExecuteWebhook("wh-1", Token, new WebhookRequestDto { Content = "hi" }, _context, _bus);
        Assert.That(executeWithOldToken, Is.InstanceOf<NotFound>(), "the previous URL must stop working");
    }

    [Test]
    public async Task GenerateToken_ProducesDistinctUrlSafeTokens()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => WebhookConfig.GenerateToken()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(tokens, Is.Unique);
            Assert.That(tokens.All(t => t.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')), Is.True,
                "the token goes in a URL path segment, so it must need no escaping");
        });
    }
}
