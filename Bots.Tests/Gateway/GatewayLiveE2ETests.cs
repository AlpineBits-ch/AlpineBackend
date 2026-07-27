using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bots.Application.Middleware;
using Bots.Contracts.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Bots.Tests.Helpers;

namespace Bots.Tests.Gateway;

/// <summary>
/// Live, opt-in end-to-end check of the Discord Gateway compat layer against a real deployed
/// instance.
/// </summary>
[TestFixture]
[Explicit("Needs real bot credentials (env vars or .e2e-credentials.local.json) and network access to a live deployment - not part of the normal test run.")]
public class GatewayLiveE2ETests
{
    [Test]
    public async Task Connect_CompletesHandshakeAndHeartbeats()
    {
        var (clientId, clientSecret, baseUrl) = LocalE2ECredentials.Load();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            Assert.Ignore("Set BOTS_E2E_CLIENT_ID/BOTS_E2E_CLIENT_SECRET or populate Bots.Tests/.e2e-credentials.local.json to run this against a live deployment.");
            return;
        }

        var gatewayUrl = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/api/discord/v10/gateway";
        var compatToken = DiscordCompatToken.Pack(clientId, clientSecret);

        using var socket = new ClientWebSocket();
        using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await socket.ConnectAsync(new Uri(gatewayUrl), connectCts.Token);
        }

        var hello = await ReceiveEnvelopeAsync(socket, TimeSpan.FromSeconds(10));
        Assert.That(hello.Op, Is.EqualTo((int)GatewayOpCode.Hello), "expected OP 10 Hello immediately on connect");

        var identify = new GatewayOutboundEnvelope<IdentifyPayload>
        {
            Op = (int)GatewayOpCode.Identify,
            D = new IdentifyPayload { Token = compatToken, Intents = 513 },
        };
        await SendEnvelopeAsync(socket, identify, TimeSpan.FromSeconds(5));

        // READY involves a real DB lookup + Redis JWT cache/exchange, so it gets a longer timeout
        // than the other round-trips.
        var ready = await ReceiveEnvelopeAsync(socket, TimeSpan.FromSeconds(20));
        Assert.Multiple(() =>
        {
            Assert.That(ready.Op, Is.EqualTo((int)GatewayOpCode.Dispatch));
            Assert.That(ready.T, Is.EqualTo("READY"));
        });

        var readyPayload = ready.D!.Value.Deserialize<ReadyPayload>()!;
        Assert.That(readyPayload.User.Id, Is.EqualTo(clientId));

        var heartbeat = new GatewayOutboundEnvelope<object?> { Op = (int)GatewayOpCode.Heartbeat, D = null };
        await SendEnvelopeAsync(socket, heartbeat, TimeSpan.FromSeconds(5));

        // GUILD_CREATE is dispatched per installed guild in parallel with the heartbeat ack, so
        // it can race ahead of it - don't assume a fixed wire order, just drain dispatches until
        // the ack itself shows up (or we time out entirely, which is a real failure).
        var sawGuildCreate = false;
        GatewayEnvelope? ack = null;
        using (var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (!overallCts.IsCancellationRequested)
            {
                var envelope = await ReceiveEnvelopeAsync(socket, TimeSpan.FromSeconds(10));
                if (envelope.Op == (int)GatewayOpCode.HeartbeatAck)
                {
                    ack = envelope;
                    break;
                }

                Assert.That(envelope.Op, Is.EqualTo((int)GatewayOpCode.Dispatch),
                    "only expected dispatches to precede the heartbeat ack");
                if (envelope.T == "GUILD_CREATE") sawGuildCreate = true;
            }
        }
        Assert.That(ack, Is.Not.Null, "never received a heartbeat ack");
        Assert.That(ack!.Op, Is.EqualTo((int)GatewayOpCode.HeartbeatAck));

        if (sawGuildCreate)
        {
            TestContext.Out.WriteLine("Received a GUILD_CREATE before the heartbeat ack - bot is installed in at least one guild.");
        }
        else
        {
            // Best-effort only: a timeout here just means the bot isn't installed anywhere, which
            // is fine.
            try
            {
                var guildCreate = await ReceiveEnvelopeAsync(socket, TimeSpan.FromSeconds(5));
                Assert.That(guildCreate.T, Is.EqualTo("GUILD_CREATE"));
                TestContext.Out.WriteLine("Received a GUILD_CREATE - bot is installed in at least one guild.");
            }
            catch (OperationCanceledException)
            {
                TestContext.Out.WriteLine("No GUILD_CREATE within 5s - bot likely isn't installed anywhere, which is fine.");
                return;
            }
        }

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", closeCts.Token);
    }

    private static async Task SendEnvelopeAsync<T>(ClientWebSocket socket, GatewayOutboundEnvelope<T> envelope, TimeSpan timeout)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
        using var cts = new CancellationTokenSource(timeout);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
    }

    private static async Task<GatewayEnvelope> ReceiveEnvelopeAsync(ClientWebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        stream.Seek(0, SeekOrigin.Begin);
        return (await JsonSerializer.DeserializeAsync<GatewayEnvelope>(stream, cancellationToken: cts.Token))!;
    }
}
