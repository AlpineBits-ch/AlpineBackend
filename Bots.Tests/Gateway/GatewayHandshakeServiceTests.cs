using System.Text.Json;
using Bots.Application.Gateway;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Response;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bots.Tests.Gateway;

/// <summary>
/// Covers the READY + GUILD_CREATE burst sent right after a successful IDENTIFY - the piece of
/// the handshake GatewayConnectionTests treats as a black box (it cans a response via
/// FakeGatewayMessageBus and only asserts dispatch *ordering*), so this drills into the payload
/// shape itself: hex color parsing, channel type mapping, and the "member is null" branch.
/// </summary>
[TestFixture]
public class GatewayHandshakeServiceTests
{
    private TestBotsContext _context = null!;
    private FakeGatewayMessageBus _bus = null!;
    private GatewayHandshakeService _service = null!;
    private FakeWebSocket _socket = null!;
    private GatewayConnectionRegistry _registry = null!;
    private GatewayConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestBotsContext(Guid.NewGuid().ToString());
        _bus = new FakeGatewayMessageBus();
        _service = new GatewayHandshakeService(_context, _bus, NullLogger<GatewayHandshakeService>.Instance);
        _socket = new FakeWebSocket();
        (_registry, _) = GatewayRegistryTestFactory.Create();
        _connection = new GatewayConnection(_socket, DummyScopeFactory(), _registry, NullLogger<GatewayConnection>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _socket.Dispose();
        await _context.DisposeAsync();
    }

    private static IServiceScopeFactory DummyScopeFactory() =>
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private async Task<BotApplication> InstallBotAsync(string botUserId, params string[] guildIds)
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = "usr_owner", BotUserId = botUserId, Name = "Test Bot", IconUrl = "https://cdn/icon.png" };
        _context.BotApplications.Add(app);
        foreach (var guildId in guildIds)
        {
            _context.BotInstallations.Add(new BotInstallation
            {
                Id = BotInstallation.GenerateId(), BotApplicationId = app.Id, GuildId = guildId,
                InstalledByUserId = "usr_admin", GuildMemberId = "gm_1", InstalledAt = DateTimeOffset.UtcNow,
            });
        }
        await _context.SaveChangesAsync();
        return app;
    }

    private static List<Bots.Contracts.Gateway.GatewayEnvelope> ParseSent(FakeWebSocket socket) =>
        socket.SentTextMessages.Select(json => System.Text.Json.JsonSerializer.Deserialize<Bots.Contracts.Gateway.GatewayEnvelope>(json)!).ToList();

    [Test]
    public async Task SendReadyAndGuildsAsync_UnknownBotUserId_ReturnsFalseAndSendsNothing()
    {
        var session = new GatewaySession { SessionId = "gwss_1", BotUserId = "usr_missing" };

        var result = await _service.SendReadyAndGuildsAsync(_connection, session, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.False);
            Assert.That(_socket.SentTextMessages, Is.Empty);
        });
    }

    [Test]
    public async Task SendReadyAndGuildsAsync_NoInstalledGuilds_SendsReadyWithEmptyGuildsList()
    {
        await InstallBotAsync("usr_bot1");
        var session = new GatewaySession { SessionId = "gwss_1", BotUserId = "usr_bot1" };

        var result = await _service.SendReadyAndGuildsAsync(_connection, session, CancellationToken.None);

        Assert.That(result, Is.True);
        var sent = ParseSent(_socket);
        Assert.That(sent, Has.Count.EqualTo(1));
        Assert.That(sent[0].T, Is.EqualTo("READY"));
        var ready = sent[0].D!.Value.Deserialize<Bots.Contracts.Gateway.Payloads.ReadyPayload>()!;
        Assert.Multiple(() =>
        {
            Assert.That(ready.User.Id, Is.EqualTo("usr_bot1"));
            Assert.That(ready.User.Bot, Is.True);
            Assert.That(ready.Guilds, Is.Empty);
            Assert.That(ready.SessionId, Is.EqualTo("gwss_1"));
        });
    }

    [Test]
    public async Task SendReadyAndGuildsAsync_GuildSnapshotUnavailable_SkipsThatGuildCreate()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        _bus.GuildSnapshotResponse = new GetGuildSnapshotForBotResponse { Guild = null };
        var session = new GatewaySession { SessionId = "gwss_1", BotUserId = "usr_bot1" };

        var result = await _service.SendReadyAndGuildsAsync(_connection, session, CancellationToken.None);

        Assert.That(result, Is.True);
        var sent = ParseSent(_socket);
        Assert.That(sent.Select(e => e.T), Does.Not.Contain("GUILD_CREATE"));
    }

    [Test]
    public async Task SendReadyAndGuildsAsync_GuildWithSelfMember_MapsRolesChannelsAndSelfIntoGuildCreate()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        _bus.GuildSnapshotResponse = new GetGuildSnapshotForBotResponse
        {
            Guild = new GuildSnapshot
            {
                Id = "gld_1", Name = "My Guild", OwnerId = "usr_owner",
                Roles = [new RoleSnapshot { Id = "rol_1", Name = "Admin", Color = "#FF00AA", Position = 1, Permissions = 8 }],
                Channels = [new ChannelSnapshot { Id = "ch_1", Name = "general", Type = "Text", Position = 0, CategoryId = null }],
                Self = new GuildMemberSummary { UserId = "usr_bot1", Nickname = "BotNick", RoleIds = ["rol_1"], JoinedAt = new DateTime(2026, 1, 1) },
            },
        };
        var session = new GatewaySession { SessionId = "gwss_1", BotUserId = "usr_bot1" };

        await _service.SendReadyAndGuildsAsync(_connection, session, CancellationToken.None);

        var guildCreate = ParseSent(_socket).Single(e => e.T == "GUILD_CREATE").D!.Value.Deserialize<Bots.Contracts.Gateway.Payloads.GuildCreatePayload>()!;
        Assert.Multiple(() =>
        {
            Assert.That(guildCreate.Id, Is.EqualTo("gld_1"));
            Assert.That(guildCreate.MemberCount, Is.EqualTo(1));
            Assert.That(guildCreate.Roles.Single().Color, Is.EqualTo(0xFF00AA));
            Assert.That(guildCreate.Channels.Single().Type, Is.EqualTo(Bots.Application.Gateway.DiscordChannelType.GuildText));
            Assert.That(guildCreate.Members.Single().Nick, Is.EqualTo("BotNick"));
        });
    }

    [Test]
    public async Task SendReadyAndGuildsAsync_GuildWithoutSelfMember_MemberCountZeroAndEmptyMembersList()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        _bus.GuildSnapshotResponse = new GetGuildSnapshotForBotResponse
        {
            Guild = new GuildSnapshot { Id = "gld_1", Name = "My Guild", OwnerId = "usr_owner", Self = null },
        };
        var session = new GatewaySession { SessionId = "gwss_1", BotUserId = "usr_bot1" };

        await _service.SendReadyAndGuildsAsync(_connection, session, CancellationToken.None);

        var guildCreate = ParseSent(_socket).Single(e => e.T == "GUILD_CREATE").D!.Value.Deserialize<Bots.Contracts.Gateway.Payloads.GuildCreatePayload>()!;
        Assert.Multiple(() =>
        {
            Assert.That(guildCreate.MemberCount, Is.EqualTo(0));
            Assert.That(guildCreate.Members, Is.Empty);
        });
    }

    [Test]
    public async Task SendReadyAndGuildsAsync_BlankRoleColor_ParsesAsZero()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        _bus.GuildSnapshotResponse = new GetGuildSnapshotForBotResponse
        {
            Guild = new GuildSnapshot
            {
                Id = "gld_1", Name = "My Guild", OwnerId = "usr_owner",
                Roles = [new RoleSnapshot { Id = "rol_1", Name = "@everyone", Color = "", Position = 0, Permissions = 0 }],
            },
        };
        var session = new GatewaySession { SessionId = "gwss_1", BotUserId = "usr_bot1" };

        await _service.SendReadyAndGuildsAsync(_connection, session, CancellationToken.None);

        var guildCreate = ParseSent(_socket).Single(e => e.T == "GUILD_CREATE").D!.Value.Deserialize<Bots.Contracts.Gateway.Payloads.GuildCreatePayload>()!;
        Assert.That(guildCreate.Roles.Single().Color, Is.EqualTo(0));
    }

    // ── R21: role metadata in the handshake ──────────────────────────────────

    [Test]
    public async Task SendReadyAndGuildsAsync_RoleMetadata_ReachesTheBotInsteadOfDefaultingToFalse()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        _bus.GuildSnapshotResponse = new GetGuildSnapshotForBotResponse
        {
            Guild = new GuildSnapshot
            {
                Id = "gld_1", Name = "My Guild", OwnerId = "usr_owner",
                Roles =
                [
                    new RoleSnapshot
                    {
                        Id = "rol_1", Name = "Staff", Color = "#101010", Position = 4,
                        Permissions = ulong.MaxValue,
                        Hoist = true, Mentionable = false, Managed = true,
                        UnicodeEmoji = "\U0001F984", BotUserId = "usr_bot1",
                    },
                ],
            },
        };
        var session = new GatewaySession { SessionId = "gwss_1", BotUserId = "usr_bot1" };

        await _service.SendReadyAndGuildsAsync(_connection, session, CancellationToken.None);

        var role = ParseSent(_socket).Single(e => e.T == "GUILD_CREATE")
            .D!.Value.Deserialize<Bots.Contracts.Gateway.Payloads.GuildCreatePayload>()!.Roles.Single();
        Assert.Multiple(() =>
        {
            Assert.That(role.Hoist, Is.True);
            Assert.That(role.Mentionable, Is.False);
            Assert.That(role.Managed, Is.True);
            Assert.That(role.UnicodeEmoji, Is.EqualTo("\U0001F984"));
            Assert.That(role.Tags!.BotId, Is.EqualTo("usr_bot1"));
            Assert.That(role.Permissions, Is.EqualTo("18446744073709551615"),
                "the full 64-bit mask survives as a decimal string; as a JSON number it would not");
        });
    }

    [Test]
    public async Task SendReadyAndGuildsAsync_RoleIcon_IsForwardedAsTheIconField()
    {
        await InstallBotAsync("usr_bot1", "gld_1");
        _bus.GuildSnapshotResponse = new GetGuildSnapshotForBotResponse
        {
            Guild = new GuildSnapshot
            {
                Id = "gld_1", Name = "My Guild", OwnerId = "usr_owner",
                Roles = [new RoleSnapshot { Id = "rol_1", Name = "Staff", Color = "", IconUrl = "https://cdn/role.png" }],
            },
        };
        var session = new GatewaySession { SessionId = "gwss_1", BotUserId = "usr_bot1" };

        await _service.SendReadyAndGuildsAsync(_connection, session, CancellationToken.None);

        var role = ParseSent(_socket).Single(e => e.T == "GUILD_CREATE")
            .D!.Value.Deserialize<Bots.Contracts.Gateway.Payloads.GuildCreatePayload>()!.Roles.Single();
        Assert.Multiple(() =>
        {
            Assert.That(role.Icon, Is.EqualTo("https://cdn/role.png"));
            Assert.That(role.Tags, Is.Null, "an unowned role carries no tags object at all");
        });
    }

    [Test]
    public async Task SendReadyAndGuildsAsync_MultipleInstalledGuilds_SendsAGuildCreatePerGuild()
    {
        await InstallBotAsync("usr_bot1", "gld_1", "gld_2");
        _bus.GuildSnapshotResponse = new GetGuildSnapshotForBotResponse
        {
            Guild = new GuildSnapshot { Id = "gld_x", Name = "Some Guild", OwnerId = "usr_owner" },
        };
        var session = new GatewaySession { SessionId = "gwss_1", BotUserId = "usr_bot1" };

        await _service.SendReadyAndGuildsAsync(_connection, session, CancellationToken.None);

        var sent = ParseSent(_socket);
        Assert.That(sent.Count(e => e.T == "GUILD_CREATE"), Is.EqualTo(2));
    }
}
