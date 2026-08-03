using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;

namespace Bots.Tests.Gateway.Handlers;

[TestFixture]
public class MessageDeletedForBotsHandlerTests
{
    private readonly FakeBotChannelVisibility _visibility = new();
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
    public async Task Handle_NoInstalledBots_DispatchesNothing()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await MessageDeletedForBotsHandler.Handle(
            new MessageDeletedForBots { GuildId = "gld_unlinked", ChannelId = "ch_1", MessageId = "m1" }, _context, registry, _visibility);

        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task Handle_InstalledBot_DispatchesMessageDeleteWithIds()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await MessageDeletedForBotsHandler.Handle(
            new MessageDeletedForBots { GuildId = "gld_1", ChannelId = "ch_1", MessageId = "m1" }, _context, registry, _visibility);

        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(botUserId, Is.EqualTo("usr_bot1"));
            Assert.That(eventName, Is.EqualTo("MESSAGE_DELETE"));
            Assert.That(data.GetProperty("id").GetString(), Is.EqualTo("m1"));
            Assert.That(data.GetProperty("channel_id").GetString(), Is.EqualTo("ch_1"));
            Assert.That(data.GetProperty("guild_id").GetString(), Is.EqualTo("gld_1"));
        });
    }

    [Test]
    public async Task Handle_MultipleInstalledBots_DispatchesToEach()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        await InstallBotAsync("usr_bot2", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await MessageDeletedForBotsHandler.Handle(
            new MessageDeletedForBots { GuildId = "gld_1", ChannelId = "ch_1", MessageId = "m1" }, _context, registry, _visibility);

        Assert.That(subscriber.Messages, Has.Count.EqualTo(2));
    }
}
