using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;

namespace Bots.Tests.Gateway.Handlers;

[TestFixture]
public class MemberRemovedForBotsHandlerTests
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
    public async Task Handle_InstalledBot_DispatchesGuildMemberRemove()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await MemberRemovedForBotsHandler.Handle(new MemberRemovedForBots { GuildId = "gld_1", UserId = "usr_gone", Reason = "Banned" }, _context, registry);

        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.That(botUserId, Is.EqualTo("usr_bot1"));
        Assert.That(eventName, Is.EqualTo("GUILD_MEMBER_REMOVE"));
        Assert.That(data.GetProperty("user").GetProperty("id").GetString(), Is.EqualTo("usr_gone"));
    }

    [Test]
    public async Task Handle_BanOrKickOrLeave_ProducesTheSameDispatchShape()
    {
        // Discord has only one GUILD_MEMBER_REMOVE event - Reason is informational and must not
        // change what's dispatched.
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registryBan, subscriberBan) = GatewayRegistryTestFactory.Create();
        var (registryLeave, subscriberLeave) = GatewayRegistryTestFactory.Create();

        await MemberRemovedForBotsHandler.Handle(new MemberRemovedForBots { GuildId = "gld_1", UserId = "usr_gone", Reason = "Banned" }, _context, registryBan);
        await MemberRemovedForBotsHandler.Handle(new MemberRemovedForBots { GuildId = "gld_1", UserId = "usr_gone", Reason = "Left" }, _context, registryLeave);

        Assert.That(subscriberBan.Messages.Single().Message.ToString(), Is.EqualTo(subscriberLeave.Messages.Single().Message.ToString()));
    }

    [Test]
    public async Task Handle_NoInstalledBots_DispatchesNothing()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await MemberRemovedForBotsHandler.Handle(new MemberRemovedForBots { GuildId = "gld_unlinked", UserId = "usr_gone", Reason = "Left" }, _context, registry);

        Assert.That(subscriber.Messages, Is.Empty);
    }
}
