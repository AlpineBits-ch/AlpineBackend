using Bots.Application.Gateway;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;

namespace Bots.Tests.Gateway;

[TestFixture]
public class InstalledBotsLookupTests
{
    private TestBotsContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestBotsContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private BotApplication AddApplication(string botUserId)
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = "usr_owner", BotUserId = botUserId, Name = "Test Bot" };
        _context.BotApplications.Add(app);
        return app;
    }

    private void AddInstallation(BotApplication app, string guildId) =>
        _context.BotInstallations.Add(new BotInstallation
        {
            Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = guildId,
            InstalledByUserId = "usr_admin", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
        });

    [Test]
    public async Task GetBotUserIdsInGuild_OneInstalledBot_ReturnsItsBotUserId()
    {
        var app = AddApplication("usr_bot1");
        AddInstallation(app, "gld_1");
        await _context.SaveChangesAsync();

        var result = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(_context, "gld_1");

        Assert.That(result, Is.EquivalentTo(new[] { "usr_bot1" }));
    }

    [Test]
    public async Task GetBotUserIdsInGuild_MultipleInstalledBots_ReturnsAll()
    {
        var app1 = AddApplication("usr_bot1");
        var app2 = AddApplication("usr_bot2");
        AddInstallation(app1, "gld_1");
        AddInstallation(app2, "gld_1");
        await _context.SaveChangesAsync();

        var result = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(_context, "gld_1");

        Assert.That(result, Is.EquivalentTo(new[] { "usr_bot1", "usr_bot2" }));
    }

    [Test]
    public async Task GetBotUserIdsInGuild_BotInstalledInDifferentGuild_IsExcluded()
    {
        var app = AddApplication("usr_bot1");
        AddInstallation(app, "gld_other");
        await _context.SaveChangesAsync();

        var result = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(_context, "gld_1");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetBotUserIdsInGuild_NoInstallations_ReturnsEmpty()
    {
        var result = await InstalledBotsLookup.GetBotUserIdsInGuildAsync(_context, "gld_1");

        Assert.That(result, Is.Empty);
    }
}
