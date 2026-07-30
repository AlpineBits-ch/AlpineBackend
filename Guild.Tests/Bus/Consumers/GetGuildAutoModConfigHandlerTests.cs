using Guild.Application.Bus.Consumers;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Entity;
using Guild.Tests.Helpers;

namespace Guild.Tests.Bus.Consumers;

/// <summary>
/// Covers GetGuildAutoModConfigHandler - resolves a channel to its guild and returns that guild's
/// AutoMod config (or a disabled default), used by Messaging before accepting a new message.
/// </summary>
[TestFixture]
public class GetGuildAutoModConfigHandlerTests
{
    private TestGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new TestGuildContext(Guid.NewGuid().ToString());

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Channel MakeChannel(string id, string guildId) => new()
    {
        Id = id, GuildId = guildId, Name = "chan", Description = "d", Type = Guild.Domain.Enums.ChannelType.Text,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task Handle_ChannelDoesNotExist_ReturnsEmptyResponse()
    {
        var response = await GetGuildAutoModConfigHandler.Handle(new GetGuildAutoModConfigRequest { ChannelId = "nonexistent" }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.GuildId, Is.Null);
            Assert.That(response.Enabled, Is.False);
        });
    }

    [Test]
    public async Task Handle_NoConfigRowYet_ReturnsGuildIdWithDisabledDefault()
    {
        _context.Channels.Add(MakeChannel("chan-1", "guild-1"));
        await _context.SaveChangesAsync();

        var response = await GetGuildAutoModConfigHandler.Handle(new GetGuildAutoModConfigRequest { ChannelId = "chan-1" }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.GuildId, Is.EqualTo("guild-1"));
            Assert.That(response.Enabled, Is.False);
        });
    }

    [Test]
    public async Task Handle_ConfigExistsButDisabled_ReturnsDisabled_IgnoringBlockedWords()
    {
        _context.Channels.Add(MakeChannel("chan-1", "guild-1"));
        _context.Set<GuildAutoModConfig>().Add(new GuildAutoModConfig
        {
            GuildId = "guild-1", Enabled = false, BlockedWords = ["shouldnotleak"], UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var response = await GetGuildAutoModConfigHandler.Handle(new GetGuildAutoModConfigRequest { ChannelId = "chan-1" }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.Enabled, Is.False);
            Assert.That(response.BlockedWords, Is.Null.Or.Empty);
        });
    }

    [Test]
    public async Task Handle_ConfigEnabled_ReturnsFullConfig()
    {
        _context.Channels.Add(MakeChannel("chan-1", "guild-1"));
        _context.Set<GuildAutoModConfig>().Add(new GuildAutoModConfig
        {
            GuildId = "guild-1", Enabled = true, BlockedWords = ["bad", "words"],
            MaxMessagesPerInterval = 5, IntervalSeconds = 10, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var response = await GetGuildAutoModConfigHandler.Handle(new GetGuildAutoModConfigRequest { ChannelId = "chan-1" }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.Enabled, Is.True);
            Assert.That(response.BlockedWords, Is.EquivalentTo(new[] { "bad", "words" }));
            Assert.That(response.MaxMessagesPerInterval, Is.EqualTo(5));
            Assert.That(response.IntervalSeconds, Is.EqualTo(10));
        });
    }
}
