using Bots.Application.Gateway.Handlers;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Events;

namespace Bots.Tests.Gateway.Handlers;

[TestFixture]
public class VoiceStateForBotsHandlerTests
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

        await VoiceStateForBotsHandler.Handle(
            new VoiceStateForBots { GuildId = "gld_unlinked", UserId = "usr_1", ChannelId = "ch_voice" }, _context, registry, _visibility);

        Assert.That(subscriber.Messages, Is.Empty);
    }

    [Test]
    public async Task Handle_UserJoinedChannel_DispatchesVoiceStateUpdateWithSynthesizedSessionId()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await VoiceStateForBotsHandler.Handle(
            new VoiceStateForBots { GuildId = "gld_1", UserId = "usr_1", ChannelId = "ch_voice", SelfMute = true, SelfVideo = true },
            _context, registry, _visibility);

        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(botUserId, Is.EqualTo("usr_bot1"));
            Assert.That(eventName, Is.EqualTo("VOICE_STATE_UPDATE"));
            Assert.That(data.GetProperty("channel_id").GetString(), Is.EqualTo("ch_voice"));
            Assert.That(data.GetProperty("session_id").GetString(), Is.EqualTo("usr_1-ch_voice"));
            Assert.That(data.GetProperty("self_mute").GetBoolean(), Is.True);
            Assert.That(data.GetProperty("self_video").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task Handle_UserLeftVoiceEntirely_SynthesizesSessionIdWithNone()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await VoiceStateForBotsHandler.Handle(
            new VoiceStateForBots { GuildId = "gld_1", UserId = "usr_1", ChannelId = null }, _context, registry, _visibility);

        var (_, _, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(data.GetProperty("channel_id").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
            Assert.That(data.GetProperty("session_id").GetString(), Is.EqualTo("usr_1-none"));
        });
    }

    [Test]
    public async Task Handle_MultipleInstalledBots_DispatchesToEach()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        await InstallBotAsync("usr_bot2", "gld_1");
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await VoiceStateForBotsHandler.Handle(
            new VoiceStateForBots { GuildId = "gld_1", UserId = "usr_1", ChannelId = "ch_voice" }, _context, registry, _visibility);

        Assert.That(subscriber.Messages, Has.Count.EqualTo(2));
    }
}
