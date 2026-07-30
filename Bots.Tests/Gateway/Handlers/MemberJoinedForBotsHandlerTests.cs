using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;
using Guild.Contracts.Bus.Response;

namespace Bots.Tests.Gateway.Handlers;

[TestFixture]
public class MemberJoinedForBotsHandlerTests
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
    public async Task Handle_NoInstalledBots_NeverCallsBus()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await MemberJoinedForBotsHandler.Handle(new MemberJoinedForBots { GuildId = "gld_1", UserId = "usr_new" }, _context, registry, _bus);

        Assert.That(_bus.Invoked, Is.Empty, "Should short-circuit before resolving member details");
        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task Handle_MemberNotFound_DispatchesNothing()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        _bus.GuildMemberResponse = new GetGuildMemberResponse { Member = null };

        await MemberJoinedForBotsHandler.Handle(new MemberJoinedForBots { GuildId = "gld_1", UserId = "usr_new" }, _context, registry, _bus);

        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task Handle_InstalledBotAndKnownMember_DispatchesGuildMemberAdd()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        var joinedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _bus.GuildMemberResponse = new GetGuildMemberResponse
        {
            Member = new GuildMemberSummary { UserId = "usr_new", Nickname = "Newbie", RoleIds = ["role_1"], JoinedAt = joinedAt, IsBot = false }
        };

        await MemberJoinedForBotsHandler.Handle(new MemberJoinedForBots { GuildId = "gld_1", UserId = "usr_new" }, _context, registry, _bus);

        Assert.That(subscriber.Messages, Has.Count.EqualTo(1));
        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages[0]);
        Assert.That(botUserId, Is.EqualTo("usr_bot1"));
        Assert.That(eventName, Is.EqualTo("GUILD_MEMBER_ADD"));
        Assert.That(data.GetProperty("nick").GetString(), Is.EqualTo("Newbie"));
        Assert.That(data.GetProperty("user").GetProperty("id").GetString(), Is.EqualTo("usr_new"));
    }
}
