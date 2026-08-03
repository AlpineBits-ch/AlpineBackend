using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;

namespace Bots.Tests.Gateway.Handlers;

[TestFixture]
public class ReactionForBotsHandlersTests
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
    public async Task ReactionCreated_InstalledBot_DispatchesMessageReactionAdd()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ReactionCreatedForBotsHandler.Handle(
            new ReactionCreatedForBots { GuildId = "gld_1", ChannelId = "ch_1", MessageId = "m1", UserId = "usr_1", Emoji = "ðŸ‘" },
            _context, registry, _visibility);

        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(botUserId, Is.EqualTo("usr_bot1"));
        Assert.That(eventName, Is.EqualTo("MESSAGE_REACTION_ADD"));
        Assert.That(data.GetProperty("emoji").GetProperty("name").GetString(), Is.EqualTo("ðŸ‘"));
    }

    [Test]
    public async Task ReactionRemoved_InstalledBot_DispatchesMessageReactionRemove()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ReactionRemovedForBotsHandler.Handle(
            new ReactionRemovedForBots { GuildId = "gld_1", ChannelId = "ch_1", MessageId = "m1", UserId = "usr_1", Emoji = "ðŸ‘" },
            _context, registry, _visibility);

        var (_, eventName, _) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(eventName, Is.EqualTo("MESSAGE_REACTION_REMOVE"));
    }

    [Test]
    public async Task ReactionCreated_NoInstalledBots_DispatchesNothing()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await ReactionCreatedForBotsHandler.Handle(
            new ReactionCreatedForBots { GuildId = "gld_unlinked", ChannelId = "ch_1", MessageId = "m1", UserId = "usr_1", Emoji = "ðŸ‘" },
            _context, registry, _visibility);

        Assert.That(subscriber.Messages, Is.Empty);
    }
}
