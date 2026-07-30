using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Bots.Application.Gateway;
using Bots.Application.Middleware;
using Bots.Contracts.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Guild.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace Bots.Tests.Gateway;

/// <summary>
/// Drives GatewayConnection.RunAsync end-to-end through a FakeWebSocket - covers the HELLO send,
/// IDENTIFY handshake (success and failure paths), the heartbeat/resume/unknown-opcode receive
/// loop, and connection teardown, none of which GatewayLiveE2ETests exercises (that suite is
/// [Explicit] and needs a real deployment). Deliberately does NOT exercise the two real-wall-clock
/// timeout branches (15s identify timeout, ~82s heartbeat timeout) - those would make the suite
/// slow for no unit-test-appropriate benefit; they're implicitly relied on being correct by
/// inspection (they mirror the well-covered timeout pattern used nowhere else in this file).
/// </summary>
[TestFixture]
public class GatewayConnectionTests
{
    private TestBotsContext _context = null!;
    private FakeGatewayMessageBus _bus = null!;
    private GatewayConnectionRegistry _registry = null!;
    private FakeSubscriber _subscriber = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestBotsContext(Guid.NewGuid().ToString());
        _bus = new FakeGatewayMessageBus();
        (_registry, _subscriber) = GatewayRegistryTestFactory.Create();

        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(new FakeHttpMessageHandler(HttpStatusCode.OK, """{"access_token":"real-jwt","expires_in":3600}""")));
        services.AddSingleton<IDistributedCache>(new FakeDistributedCache());
        services.AddScoped<BotTokenTranslator>();
        services.AddSingleton<Bots.Infrastructure.Persistence.MicroserviceContext>(_context);
        services.AddSingleton<IMessageBus>(_bus);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<GatewayHandshakeService>();
        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private GatewayConnection MakeConnection(FakeWebSocket socket) =>
        new(socket, _provider.GetRequiredService<IServiceScopeFactory>(), _registry, NullLogger<GatewayConnection>.Instance);

    private async Task<BotApplication> InstallBotAsync(string botUserId, params string[] guildIds)
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = "usr_owner", BotUserId = botUserId, Name = "Test Bot" };
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

    private static string IdentifyJson(string token) =>
        JsonSerializer.Serialize(new GatewayOutboundEnvelope<IdentifyPayload> { Op = (int)GatewayOpCode.Identify, D = new IdentifyPayload { Token = token, Intents = 513 } });

    private static List<GatewayEnvelope> ParseSent(FakeWebSocket socket) =>
        socket.SentTextMessages.Select(json => JsonSerializer.Deserialize<GatewayEnvelope>(json)!).ToList();

    [Test]
    public async Task RunAsync_FirstMessage_SendsHelloOp()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueClose(); // makes WaitForIdentifyAsync fail fast (envelope is null -> Close 4003)
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        var sent = ParseSent(socket);
        Assert.That(sent[0].Op, Is.EqualTo((int)GatewayOpCode.Hello));
    }

    [Test]
    public async Task RunAsync_ClientClosesBeforeIdentifying_EchoesCloseHandshakeWithoutASecondCloseFrame()
    {
        // The client's own close frame is echoed back (completing the four-way close handshake)
        // inside ReceiveEnvelopeAsync itself, before WaitForIdentifyAsync's own "not authenticated"
        // CloseAsync(4003) branch even runs - by then the socket is no longer Open, so that second
        // close is correctly skipped rather than attempting (and failing) to send two close frames.
        var socket = new FakeWebSocket();
        socket.EnqueueClose();
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        Assert.That(socket.Closes.Single().Status, Is.EqualTo(WebSocketCloseStatus.NormalClosure));
    }

    [Test]
    public async Task RunAsync_WrongOpcodeInsteadOfIdentify_ClosesWithNotAuthenticated()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueText(JsonSerializer.Serialize(new GatewayOutboundEnvelope<object?> { Op = (int)GatewayOpCode.Heartbeat, D = null }));
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        Assert.That(socket.Closes.Single().Status, Is.EqualTo((WebSocketCloseStatus)4003));
    }

    [Test]
    public async Task RunAsync_IdentifyWithBlankToken_ClosesWithNotAuthenticated()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueText(IdentifyJson(""));
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        Assert.That(socket.Closes.Single().Status, Is.EqualTo((WebSocketCloseStatus)4003));
    }

    [Test]
    public async Task RunAsync_IdentifyWithUnexchangeableToken_ClosesWithAuthenticationFailed()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueText(IdentifyJson("not-a-valid-packed-token!!!"));
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        Assert.That(socket.Closes.Single().Status, Is.EqualTo((WebSocketCloseStatus)4004));
    }

    [Test]
    public async Task RunAsync_IdentifySucceedsButBotAppMissing_ClosesWithAuthenticationFailedAndDoesNotRegister()
    {
        var packed = DiscordCompatToken.Pack("usr_bot1", "secret1");
        var socket = new FakeWebSocket();
        socket.EnqueueText(IdentifyJson(packed));
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(socket.Closes.Single().Status, Is.EqualTo((WebSocketCloseStatus)4004));
            Assert.That(_registry.LocalConnections, Is.Empty);
        });
    }

    [Test]
    public async Task RunAsync_FullHandshake_SendsReadyThenGuildCreateForEachInstalledGuild()
    {
        var packed = DiscordCompatToken.Pack("usr_bot1", "secret1");
        await InstallBotAsync("usr_bot1", "gld_1");
        _bus.GuildSnapshotResponse = new GetGuildSnapshotForBotResponse
        {
            Guild = new GuildSnapshot { Id = "gld_1", Name = "My Guild", OwnerId = "usr_owner" },
        };

        var socket = new FakeWebSocket();
        socket.EnqueueText(IdentifyJson(packed));
        socket.EnqueueClose();
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        var sent = ParseSent(socket);
        Assert.Multiple(() =>
        {
            Assert.That(sent[0].Op, Is.EqualTo((int)GatewayOpCode.Hello));
            Assert.That(sent[1].T, Is.EqualTo("READY"));
            Assert.That(sent[2].T, Is.EqualTo("GUILD_CREATE"));
        });
    }

    [Test]
    public async Task RunAsync_FullHandshake_RegistersThenDeregistersOnClose()
    {
        var packed = DiscordCompatToken.Pack("usr_bot1", "secret1");
        await InstallBotAsync("usr_bot1");

        var socket = new FakeWebSocket();
        socket.EnqueueText(IdentifyJson(packed));
        socket.EnqueueClose();
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        // by the time RunAsync returns, the finally block has already deregistered it
        Assert.That(_registry.LocalConnections, Is.Empty);
    }

    [Test]
    public async Task RunAsync_HeartbeatOpcode_RespondsWithHeartbeatAck()
    {
        var packed = DiscordCompatToken.Pack("usr_bot1", "secret1");
        await InstallBotAsync("usr_bot1");

        var socket = new FakeWebSocket();
        socket.EnqueueText(IdentifyJson(packed));
        socket.EnqueueText(JsonSerializer.Serialize(new GatewayOutboundEnvelope<object?> { Op = (int)GatewayOpCode.Heartbeat, D = null }));
        socket.EnqueueClose();
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        var sent = ParseSent(socket);
        Assert.That(sent.Select(e => e.Op), Does.Contain((int)GatewayOpCode.HeartbeatAck));
    }

    [Test]
    public async Task RunAsync_ResumeOpcode_RespondsWithInvalidSession()
    {
        var packed = DiscordCompatToken.Pack("usr_bot1", "secret1");
        await InstallBotAsync("usr_bot1");

        var socket = new FakeWebSocket();
        socket.EnqueueText(IdentifyJson(packed));
        socket.EnqueueText(JsonSerializer.Serialize(new GatewayOutboundEnvelope<ResumePayload> { Op = (int)GatewayOpCode.Resume, D = new ResumePayload() }));
        socket.EnqueueClose();
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        var sent = ParseSent(socket);
        Assert.That(sent.Select(e => e.Op), Does.Contain((int)GatewayOpCode.InvalidSession));
    }

    [Test]
    public async Task RunAsync_UnhandledClientOpcode_IsIgnoredAndConnectionKeepsRunning()
    {
        var packed = DiscordCompatToken.Pack("usr_bot1", "secret1");
        await InstallBotAsync("usr_bot1");

        var socket = new FakeWebSocket();
        socket.EnqueueText(IdentifyJson(packed));
        // OP 3 Presence Update - real Discord tolerates and ignores updates it doesn't act on.
        socket.EnqueueText(JsonSerializer.Serialize(new GatewayOutboundEnvelope<object?> { Op = (int)GatewayOpCode.PresenceUpdate, D = null }));
        socket.EnqueueText(JsonSerializer.Serialize(new GatewayOutboundEnvelope<object?> { Op = (int)GatewayOpCode.Heartbeat, D = null }));
        socket.EnqueueClose();
        var connection = MakeConnection(socket);

        await connection.RunAsync(CancellationToken.None);

        // the unhandled opcode didn't crash the loop - the heartbeat right after it still got acked
        var sent = ParseSent(socket);
        Assert.That(sent.Select(e => e.Op), Does.Contain((int)GatewayOpCode.HeartbeatAck));
    }

    [Test]
    public async Task SendDispatchAsync_SocketNotOpen_DoesNothing()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueClose();
        var connection = MakeConnection(socket);
        await connection.RunAsync(CancellationToken.None); // socket is now Closed

        await connection.SendDispatchAsync("SOME_EVENT", new { });

        // no additional message sent beyond HELLO
        Assert.That(socket.SentTextMessages, Has.Count.EqualTo(1));
    }
}
