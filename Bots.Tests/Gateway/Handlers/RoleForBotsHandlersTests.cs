using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;
using Guild.Contracts.Bus.Response;

namespace Bots.Tests.Gateway.Handlers;

/// <summary>Covers R15's bot half: the role lifecycle reaching installed bots as Discord's
/// GUILD_ROLE_CREATE / GUILD_ROLE_UPDATE / GUILD_ROLE_DELETE, in Discord's envelope shape.</summary>
[TestFixture]
public class RoleForBotsHandlersTests
{
    private TestBotsContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestBotsContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task InstallBotAsync(string botUserId, string guildId)
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = "usr_owner", BotUserId = botUserId, Name = "Test Bot" };
        _context.BotApplications.Add(app);
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = guildId,
            InstalledByUserId = "usr_admin", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private static RoleSnapshot Snapshot(string id = "rol_1") => new()
    {
        Id = id, Name = "Moderators", Color = "#FF00AA", Position = 3,
        Permissions = 1234, Hoist = true, Mentionable = false, Managed = false,
    };

    [Test]
    public async Task RoleCreated_InstalledBot_DispatchesGuildRoleCreateInDiscordEnvelope()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await RoleCreatedForBotsHandler.Handle(
            new RoleCreatedForBots { GuildId = "gld_1", Role = Snapshot() }, _context, registry);

        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(botUserId, Is.EqualTo("usr_bot1"));
            Assert.That(eventName, Is.EqualTo("GUILD_ROLE_CREATE"));
            Assert.That(data.GetProperty("guild_id").GetString(), Is.EqualTo("gld_1"));
            Assert.That(data.GetProperty("role").GetProperty("id").GetString(), Is.EqualTo("rol_1"));
            Assert.That(data.GetProperty("role").GetProperty("color").GetInt32(), Is.EqualTo(0xFF00AA));
            Assert.That(data.GetProperty("role").GetProperty("permissions").GetString(), Is.EqualTo("1234"),
                "Discord serializes the permission bitfield as a decimal string, not a number");
            Assert.That(data.GetProperty("role").GetProperty("hoist").GetBoolean(), Is.True);
            Assert.That(data.GetProperty("role").GetProperty("mentionable").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task RoleUpdated_InstalledBot_DispatchesGuildRoleUpdate()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        var role = Snapshot();
        role.Name = "Renamed";

        await RoleUpdatedForBotsHandler.Handle(
            new RoleUpdatedForBots { GuildId = "gld_1", Role = role }, _context, registry);

        var (_, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(eventName, Is.EqualTo("GUILD_ROLE_UPDATE"));
            Assert.That(data.GetProperty("role").GetProperty("name").GetString(), Is.EqualTo("Renamed"));
        });
    }

    [Test]
    public async Task RoleDeleted_InstalledBot_DispatchesGuildRoleDeleteWithIdOnly()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await RoleDeletedForBotsHandler.Handle(
            new RoleDeletedForBots { GuildId = "gld_1", RoleId = "rol_1" }, _context, registry);

        var (_, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(eventName, Is.EqualTo("GUILD_ROLE_DELETE"));
            Assert.That(data.GetProperty("role_id").GetString(), Is.EqualTo("rol_1"));
            Assert.That(data.TryGetProperty("role", out _), Is.False,
                "there is no role object left to send, and Discord sends none");
        });
    }

    [Test]
    public async Task RoleCreated_NoInstalledBots_DispatchesNothing()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await RoleCreatedForBotsHandler.Handle(
            new RoleCreatedForBots { GuildId = "gld_unlinked", Role = Snapshot() }, _context, registry);

        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task RoleCreated_BotInstalledElsewhere_IsNotInTheAudience()
    {
        await InstallBotAsync("usr_bot1", "gld_other");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await RoleCreatedForBotsHandler.Handle(
            new RoleCreatedForBots { GuildId = "gld_1", Role = Snapshot() }, _context, registry);

        Assert.That(subscriber.Messages, Is.Empty,
            "installation in one guild is not visibility of another guild's roles");
    }

    [Test]
    public async Task RoleUpdated_TwoInstalledBots_DispatchesToBoth()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        await InstallBotAsync("usr_bot2", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await RoleUpdatedForBotsHandler.Handle(
            new RoleUpdatedForBots { GuildId = "gld_1", Role = Snapshot() }, _context, registry);

        Assert.That(subscriber.Messages, Has.Count.EqualTo(2));
        var botIds = subscriber.Messages.Select(m => DispatchAssertions.Parse(m).BotUserId);
        Assert.That(botIds, Is.EquivalentTo(new[] { "usr_bot1", "usr_bot2" }));
    }

    [Test]
    public async Task RoleCreated_ManagedBotRole_CarriesDiscordTags()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        var role = Snapshot();
        role.Managed = true;
        role.BotUserId = "usr_bot1";

        await RoleCreatedForBotsHandler.Handle(
            new RoleCreatedForBots { GuildId = "gld_1", Role = role }, _context, registry);

        var (_, _, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        var payload = data.GetProperty("role");
        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("managed").GetBoolean(), Is.True);
            Assert.That(payload.GetProperty("tags").GetProperty("bot_id").GetString(), Is.EqualTo("usr_bot1"));
            Assert.That(payload.GetProperty("tags").TryGetProperty("integration_id", out _), Is.False,
                "an absent tag is omitted, not written as null - Discord gives an explicit null its own meaning");
        });
    }

    [Test]
    public async Task RoleCreated_OrdinaryRole_OmitsTagsEntirely()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await RoleCreatedForBotsHandler.Handle(
            new RoleCreatedForBots { GuildId = "gld_1", Role = Snapshot() }, _context, registry);

        var (_, _, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(data.GetProperty("role").TryGetProperty("tags", out _), Is.False);
    }
}
