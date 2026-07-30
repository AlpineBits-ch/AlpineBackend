using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;
using Guild.Contracts.Bus.Response;

namespace Bots.Tests.Gateway.Handlers;

[TestFixture]
public class MemberUpdatedForBotsHandlerTests
{
    private TestBotsContext _context = null!;
    private FakeGatewayMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestBotsContext(Guid.NewGuid().ToString());
        _bus = new FakeGatewayMessageBus();
    }

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
    public async Task Handle_NoInstalledBots_NeverResolvesMember()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await MemberUpdatedForBotsHandler.Handle(
            new MemberUpdatedForBots { GuildId = "gld_unlinked", UserId = "usr_1" }, _context, registry, _bus);

        Assert.That(_bus.Invoked, Is.Empty);
        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task Handle_MemberNoLongerFound_DispatchesNothing()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        _bus.GuildMemberResponse = new GetGuildMemberResponse { Member = null };

        await MemberUpdatedForBotsHandler.Handle(
            new MemberUpdatedForBots { GuildId = "gld_1", UserId = "usr_gone" }, _context, registry, _bus);

        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task Handle_MemberFound_DispatchesGuildMemberUpdateWithCurrentState()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        var joinedAt = new DateTime(2026, 1, 1);
        var mutedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        _bus.GuildMemberResponse = new GetGuildMemberResponse
        {
            Member = new GuildMemberSummary
            {
                UserId = "usr_member", Nickname = "Nicky", RoleIds = ["rol_1", "rol_2"],
                JoinedAt = joinedAt, IsBot = false, MutedUntil = mutedUntil,
            },
        };

        await MemberUpdatedForBotsHandler.Handle(
            new MemberUpdatedForBots { GuildId = "gld_1", UserId = "usr_member" }, _context, registry, _bus);

        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(botUserId, Is.EqualTo("usr_bot1"));
            Assert.That(eventName, Is.EqualTo("GUILD_MEMBER_UPDATE"));
            Assert.That(data.GetProperty("nick").GetString(), Is.EqualTo("Nicky"));
            Assert.That(data.GetProperty("roles").GetArrayLength(), Is.EqualTo(2));
            Assert.That(data.GetProperty("user").GetProperty("id").GetString(), Is.EqualTo("usr_member"));
        });
    }

    [Test]
    public async Task Handle_MultipleInstalledBots_DispatchesToEach()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        await InstallBotAsync("usr_bot2", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        _bus.GuildMemberResponse = new GetGuildMemberResponse
        {
            Member = new GuildMemberSummary { UserId = "usr_member", JoinedAt = DateTime.UtcNow },
        };

        await MemberUpdatedForBotsHandler.Handle(
            new MemberUpdatedForBots { GuildId = "gld_1", UserId = "usr_member" }, _context, registry, _bus);

        Assert.That(subscriber.Messages, Has.Count.EqualTo(2));
    }
}
