using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;

namespace Bots.Tests.Gateway.Handlers;

[TestFixture]
public class ThreadForBotsHandlersTests
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
    public async Task ThreadCreated_NoInstalledBots_DispatchesNothing()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ThreadCreatedForBotsHandler.Handle(
            new ThreadCreatedForBots { GuildId = "gld_unlinked", ChannelId = "ch_thread", ParentChannelId = "ch_parent", Name = "new-thread" },
            _context, registry);

        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task ThreadCreated_InstalledBot_DispatchesThreadCreateNotArchived()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ThreadCreatedForBotsHandler.Handle(
            new ThreadCreatedForBots { GuildId = "gld_1", ChannelId = "ch_thread", ParentChannelId = "ch_parent", Name = "new-thread" },
            _context, registry);

        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(botUserId, Is.EqualTo("usr_bot1"));
            Assert.That(eventName, Is.EqualTo("THREAD_CREATE"));
            Assert.That(data.GetProperty("id").GetString(), Is.EqualTo("ch_thread"));
            Assert.That(data.GetProperty("parent_id").GetString(), Is.EqualTo("ch_parent"));
            Assert.That(data.GetProperty("name").GetString(), Is.EqualTo("new-thread"));
            Assert.That(data.GetProperty("thread_metadata").GetProperty("archived").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task ThreadUpdated_NoInstalledBots_DispatchesNothing()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ThreadUpdatedForBotsHandler.Handle(
            new ThreadUpdatedForBots { GuildId = "gld_unlinked", ChannelId = "ch_thread", ParentChannelId = "ch_parent", Name = "renamed", Archived = true },
            _context, registry);

        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task ThreadUpdated_Archived_DispatchesThreadUpdateWithArchivedTrue()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ThreadUpdatedForBotsHandler.Handle(
            new ThreadUpdatedForBots { GuildId = "gld_1", ChannelId = "ch_thread", ParentChannelId = "ch_parent", Name = "archived-thread", Archived = true },
            _context, registry);

        var (_, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(eventName, Is.EqualTo("THREAD_UPDATE"));
        Assert.That(data.GetProperty("thread_metadata").GetProperty("archived").GetBoolean(), Is.True);
    }

    [Test]
    public async Task ThreadUpdated_MultipleInstalledBots_DispatchesToEach()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        await InstallBotAsync("usr_bot2", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ThreadUpdatedForBotsHandler.Handle(
            new ThreadUpdatedForBots { GuildId = "gld_1", ChannelId = "ch_thread", ParentChannelId = "ch_parent", Name = "t", Archived = false },
            _context, registry);

        Assert.That(subscriber.Messages, Has.Count.EqualTo(2));
    }
}
