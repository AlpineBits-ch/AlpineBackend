using System.Security.Claims;
using System.Text.Json;
using Bots.Application.Endpoints;
using Bots.Application.Gateway;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints;

[TestFixture]
public class BotCommandEndpointTests
{
    private TestBotsContext _context = null!;
    private FakeGatewayMessageBus _bus = null!;
    private PendingInteractionStore _pendingStore = null!;
    private BotCommandEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestBotsContext(Guid.NewGuid().ToString());
        _bus = new FakeGatewayMessageBus();
        _pendingStore = new PendingInteractionStore(new FakeDistributedCache());
        _endpoint = new BotCommandEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private async Task<BotApplication> InstallBotAsync(string botUserId, string guildId, bool enabled = true)
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = "usr_owner", BotUserId = botUserId, Name = "Test Bot", IsEnabled = enabled };
        _context.BotApplications.Add(app);
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = guildId,
            InstalledByUserId = "usr_admin", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
        return app;
    }

    private BotCommand AddCommand(BotApplication app, string name, string? guildId = null, string optionsJson = "[]")
    {
        var command = new BotCommand { Id = BotCommand.GenerateId(), BotApplicationId = app.Id, Name = name, Description = "desc", GuildId = guildId, OptionsJson = optionsJson };
        _context.BotCommands.Add(command);
        return command;
    }

    private static JsonElement AsJson(object result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        return JsonSerializer.SerializeToElement(value);
    }

    // ── GetCommandsForGuildAsync ─────────────────────────────────────────────

    [Test]
    public async Task GetCommands_NoBotsInstalled_ReturnsEmptyArray()
    {
        var result = await _endpoint.GetCommandsForGuildAsync("gld_1", _context, MakeUser("usr_member"), _bus);

        var json = AsJson(result);
        Assert.That(json.GetArrayLength(), Is.EqualTo(0));
    }

    [Test]
    public async Task GetCommands_GlobalAndMatchingGuildScopedCommands_AreReturned()
    {
        var app = await InstallBotAsync("usr_bot1", "gld_1");
        AddCommand(app, "ping");
        AddCommand(app, "guild-only", guildId: "gld_1");
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetCommandsForGuildAsync("gld_1", _context, MakeUser("usr_member"), _bus);

        var json = AsJson(result);
        Assert.That(json.GetArrayLength(), Is.EqualTo(2));
        var names = json.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.That(names, Is.EquivalentTo(new[] { "ping", "guild-only" }));
    }

    [Test]
    public async Task GetCommands_GuildScopedCommandForDifferentGuild_IsExcluded()
    {
        var app = await InstallBotAsync("usr_bot1", "gld_1");
        AddCommand(app, "other-guild-only", guildId: "gld_2");
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetCommandsForGuildAsync("gld_1", _context, MakeUser("usr_member"), _bus);

        var json = AsJson(result);
        Assert.That(json.GetArrayLength(), Is.EqualTo(0));
    }

    // ── InvokeCommandAsync ────────────────────────────────────────────────────

    [Test]
    public async Task Invoke_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var (registry, _) = GatewayRegistryTestFactory.Create();

        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", new InvokeCommandDto(), anonymous, _context, _bus, registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task Invoke_NoSendMessagesPermission_ReturnsForbid()
    {
        var (registry, _) = GatewayRegistryTestFactory.Create();
        _bus.PermissionResponse = new() { IsAllowed = false, Permission = Guild.Contracts.ExternalPermission.SendMessages };

        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", new InvokeCommandDto { BotUserId = "usr_bot1", CommandName = "ping" },
            MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // ── UseApplicationCommands gate ──────────────────────────────────────────

    [Test]
    public async Task Invoke_DeniedUseApplicationCommands_ReturnsForbidEvenWhenTheMemberMaySpeak()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        _bus.ChannelPermissionOverrides[Guild.Contracts.ExternalPermission.SendMessages] = true;
        _bus.ChannelPermissionOverrides[Guild.Contracts.ExternalPermission.UseApplicationCommands] = false;

        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", new InvokeCommandDto { BotUserId = "usr_bot1", CommandName = "ping" },
            MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(subscriber.Messages, Is.Empty, "a refused invocation must reach no bot");
        });
    }

    /// <summary>The @everyone default holds UseApplicationCommands, and the back-fill migration set
    /// it on every pre-existing @everyone role - so enforcement is invisible to guilds that were
    /// working yesterday. This is that claim expressed at this layer: both answers yes, invocation
    /// proceeds.</summary>
    [Test]
    public async Task Invoke_DefaultEveryoneHoldsBothPermissions_Proceeds()
    {
        var app = await InstallBotAsync("usr_bot1", "gld_1");
        AddCommand(app, "ping");
        await _context.SaveChangesAsync();
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        _bus.ChannelPermissionOverrides[Guild.Contracts.ExternalPermission.SendMessages] = true;
        _bus.ChannelPermissionOverrides[Guild.Contracts.ExternalPermission.UseApplicationCommands] = true;

        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", new InvokeCommandDto { BotUserId = "usr_bot1", CommandName = "ping" },
            MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Accepted>());
            Assert.That(subscriber.Messages, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Invoke_UnknownBot_ReturnsNotFound()
    {
        var (registry, _) = GatewayRegistryTestFactory.Create();

        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", new InvokeCommandDto { BotUserId = "usr_missing", CommandName = "ping" },
            MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task Invoke_DisabledBot_ReturnsNotFound()
    {
        await InstallBotAsync("usr_bot1", "gld_1", enabled: false);
        var (registry, _) = GatewayRegistryTestFactory.Create();

        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", new InvokeCommandDto { BotUserId = "usr_bot1", CommandName = "ping" },
            MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task Invoke_BotNotInstalledInThisGuild_ReturnsNotFound()
    {
        await InstallBotAsync("usr_bot1", "gld_other");
        var (registry, _) = GatewayRegistryTestFactory.Create();

        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", new InvokeCommandDto { BotUserId = "usr_bot1", CommandName = "ping" },
            MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task Invoke_UnknownCommand_ReturnsNotFound()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, _) = GatewayRegistryTestFactory.Create();

        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", new InvokeCommandDto { BotUserId = "usr_bot1", CommandName = "missing-cmd" },
            MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task Invoke_ValidCommand_DispatchesInteractionCreateAndSavesPendingInteraction()
    {
        var app = await InstallBotAsync("usr_bot1", "gld_1");
        AddCommand(app, "ping", optionsJson: """[{"name":"target","type":6}]""");
        await _context.SaveChangesAsync();
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        var dto = new InvokeCommandDto
        {
            BotUserId = "usr_bot1", CommandName = "ping",
            Options = [new InvokeCommandOptionDto { Name = "target", Value = JsonSerializer.SerializeToElement("usr_someone") }],
        };
        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", dto, MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<Accepted>());
        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(botUserId, Is.EqualTo("usr_bot1"));
            Assert.That(eventName, Is.EqualTo("INTERACTION_CREATE"));
            Assert.That(data.GetProperty("data").GetProperty("name").GetString(), Is.EqualTo("ping"));
            Assert.That(data.GetProperty("data").GetProperty("options")[0].GetProperty("type").GetInt32(), Is.EqualTo(6));
            Assert.That(data.GetProperty("channel_id").GetString(), Is.EqualTo("ch_1"));
            Assert.That(data.GetProperty("guild_id").GetString(), Is.EqualTo("gld_1"));
        });

        var token = data.GetProperty("token").GetString()!;
        var pending = await _pendingStore.GetAsync(token);
        Assert.That(pending, Is.Not.Null);
        Assert.That(pending!.CommandName, Is.EqualTo("ping"));
        Assert.That(pending.InvokingUserId, Is.EqualTo("usr_caller"));
    }

    [Test]
    public async Task Invoke_UnknownOptionName_FallsBackToStringType()
    {
        var app = await InstallBotAsync("usr_bot1", "gld_1");
        AddCommand(app, "ping", optionsJson: "[]");
        await _context.SaveChangesAsync();
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        var dto = new InvokeCommandDto
        {
            BotUserId = "usr_bot1", CommandName = "ping",
            Options = [new InvokeCommandOptionDto { Name = "unrecognized", Value = JsonSerializer.SerializeToElement("x") }],
        };
        await _endpoint.InvokeCommandAsync("gld_1", "ch_1", dto, MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        var (_, _, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(data.GetProperty("data").GetProperty("options")[0].GetProperty("type").GetInt32(), Is.EqualTo(3));
    }

    [Test]
    public async Task Invoke_MalformedOptionsSchema_DoesNotThrowAndFallsBackToStringType()
    {
        var app = await InstallBotAsync("usr_bot1", "gld_1");
        AddCommand(app, "ping", optionsJson: "not-valid-json");
        await _context.SaveChangesAsync();
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        var dto = new InvokeCommandDto
        {
            BotUserId = "usr_bot1", CommandName = "ping",
            Options = [new InvokeCommandOptionDto { Name = "x", Value = JsonSerializer.SerializeToElement("y") }],
        };
        var result = await _endpoint.InvokeCommandAsync("gld_1", "ch_1", dto, MakeUser("usr_caller"), _context, _bus, registry, _pendingStore);

        Assert.That(result, Is.InstanceOf<Accepted>());
        var (_, _, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(data.GetProperty("data").GetProperty("options")[0].GetProperty("type").GetInt32(), Is.EqualTo(3));
    }
}
