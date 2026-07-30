using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;

namespace Bots.Tests.Gateway.Handlers;

[TestFixture]
public class ChannelForBotsHandlersTests
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

    [Test]
    public async Task ChannelCreated_InstalledBot_DispatchesChannelCreate()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ChannelCreatedForBotsHandler.Handle(
            new ChannelCreatedForBots { ChannelId = "chan_1", GuildId = "gld_1", Name = "general", Type = "Text", Position = 0 },
            _context, registry);

        Assert.That(subscriber.Messages, Has.Count.EqualTo(1));
        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages[0]);
        Assert.That(botUserId, Is.EqualTo("usr_bot1"));
        Assert.That(eventName, Is.EqualTo("CHANNEL_CREATE"));
        Assert.That(data.GetProperty("id").GetString(), Is.EqualTo("chan_1"));
    }

    [Test]
    public async Task ChannelCreated_NoInstalledBots_DispatchesNothing()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ChannelCreatedForBotsHandler.Handle(
            new ChannelCreatedForBots { ChannelId = "chan_1", GuildId = "gld_unlinked", Name = "general", Type = "Text" },
            _context, registry);

        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task ChannelUpdated_InstalledBot_DispatchesChannelUpdate()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ChannelUpdatedForBotsHandler.Handle(
            new ChannelUpdatedForBots { ChannelId = "chan_1", GuildId = "gld_1", Name = "renamed", Type = "Text", Position = 1 },
            _context, registry);

        var (_, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(eventName, Is.EqualTo("CHANNEL_UPDATE"));
        Assert.That(data.GetProperty("name").GetString(), Is.EqualTo("renamed"));
    }

    [Test]
    public async Task ChannelDeleted_InstalledBot_DispatchesChannelDelete()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ChannelDeletedForBotsHandler.Handle(
            new ChannelDeletedForBots { ChannelId = "chan_1", GuildId = "gld_1" },
            _context, registry);

        var (_, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(eventName, Is.EqualTo("CHANNEL_DELETE"));
        Assert.That(data.GetProperty("id").GetString(), Is.EqualTo("chan_1"));
    }

    [Test]
    public async Task ChannelCreated_TwoInstalledBots_DispatchesToBoth()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        await InstallBotAsync("usr_bot2", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ChannelCreatedForBotsHandler.Handle(
            new ChannelCreatedForBots { ChannelId = "chan_1", GuildId = "gld_1", Name = "general", Type = "Text" },
            _context, registry);

        Assert.That(subscriber.Messages, Has.Count.EqualTo(2));
        var botIds = subscriber.Messages.Select(m => DispatchAssertions.Parse(m).BotUserId);
        Assert.That(botIds, Is.EquivalentTo(new[] { "usr_bot1", "usr_bot2" }));
    }
}
