using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bots.Application.Gateway;
using Bots.Application.Middleware;
using Bots.Contracts.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Bots.Tests.Helpers;

namespace Bots.Tests.Gateway;

/// <summary>
/// Live, opt-in end-to-end checks of the Discord Gateway compat layer against a real deployed
/// instance. Deliberately NOT part of the normal test run (see [Explicit]) - a full local
/// multi-service E2E harness (Identity + Guild + Bots + Postgres + Redis + RabbitMQ all running
/// together) is out of scope for this repo's test suite, so this drives the real handshake
/// against an already-deployed instance instead. Trigger explicitly once you have real bot
/// credentials, either exported...
///
///   BOTS_E2E_CLIENT_ID=user_xxx BOTS_E2E_CLIENT_SECRET=xxx dotnet test --filter FullyQualifiedName~GatewayLiveE2ETests
///
/// ...or dropped into Bots.Tests/.e2e-credentials.local.json (git-ignored - see LocalE2ECredentials).
/// BOTS_E2E_BASE_URL / the file's "baseUrl" default to https://api.venta.gg. See also
/// Bots.Tests/e2e-discordjs/ for the same handshake check driven by the real discord.js library
/// instead of this hand-rolled ClientWebSocket client.
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

        using var socket = new ClientWebSocket();
        var (readyPayload, _) = await ConnectAndIdentifyAsync(socket, baseUrl, clientId, clientSecret);
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
            // Best-effort only: a timeout here just means the bot isn't installed anywhere, which is
            // fine. Cancelling a pending ClientWebSocket.ReceiveAsync aborts the connection, so this
            // must be the last thing attempted before close.
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

    /// <summary>
    /// Full round trip: the bot sends itself a message over the existing REST compat surface,
    /// then the SAME Gateway connection must receive a MESSAGE_CREATE dispatch for it - proves
    /// the Guild -> Bots.Application dispatch-fan-out path actually delivers, not just that the
    /// handshake completes. Requires the bot to already be installed in at least one guild with a
    /// text channel (ignored, not failed, if not - this test can't create that state itself).
    /// </summary>
    [Test]
    public async Task SendMessage_ViaRestApi_IsReceivedAsMessageCreateDispatch()
    {
        var (clientId, clientSecret, baseUrl) = LocalE2ECredentials.Load();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            Assert.Ignore("Set BOTS_E2E_CLIENT_ID/BOTS_E2E_CLIENT_SECRET or populate Bots.Tests/.e2e-credentials.local.json to run this against a live deployment.");
            return;
        }

        using var socket = new ClientWebSocket();
        await ConnectAndIdentifyAsync(socket, baseUrl, clientId, clientSecret);

        var location = await FindTextChannelFromGuildCreatesAsync(socket, TimeSpan.FromSeconds(10));
        if (location is null)
        {
            Assert.Ignore("Bot isn't installed in any guild with a text channel - install it somewhere first to run this check.");
            return;
        }

        var marker = $"e2e-check-{Guid.NewGuid():N}";
        await PostMessageAsync(baseUrl, clientId, clientSecret, location.Value.ChannelId, marker);

        var messageCreate = await FindDispatchAsync(socket, "MESSAGE_CREATE", TimeSpan.FromSeconds(15));
        Assert.That(messageCreate, Is.Not.Null, "never received a MESSAGE_CREATE dispatch for the message we just sent");

        var payload = messageCreate!.D!.Value.Deserialize<MessageCreatePayload>()!;
        Assert.Multiple(() =>
        {
            Assert.That(payload.Content, Is.EqualTo(marker));
            Assert.That(payload.ChannelId, Is.EqualTo(location.Value.ChannelId));
            Assert.That(payload.Author.Id, Is.EqualTo(clientId));
        });

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", closeCts.Token);
    }

    /// <summary>
    /// Full slash-command round trip, immediate response: register a command via the Discord-
    /// compat surface, invoke it via the native endpoint (using the bot's own JWT to stand in for
    /// "a human invoked this" - the dispatch path doesn't care whose user id it is), assert
    /// INTERACTION_CREATE arrives with the right shape, then respond immediately (type 4) and
    /// assert the real message shows up as MESSAGE_CREATE - same pattern as the message round
    /// trip, extended one hop further.
    /// </summary>
    [Test]
    public async Task RegisterCommand_Invoke_ReceivesInteractionCreate_ThenImmediateCallbackCreatesMessage()
    {
        var (clientId, clientSecret, baseUrl) = LocalE2ECredentials.Load();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            Assert.Ignore("Set BOTS_E2E_CLIENT_ID/BOTS_E2E_CLIENT_SECRET or populate Bots.Tests/.e2e-credentials.local.json to run this against a live deployment.");
            return;
        }

        using var socket = new ClientWebSocket();
        await ConnectAndIdentifyAsync(socket, baseUrl, clientId, clientSecret);

        var location = await FindTextChannelFromGuildCreatesAsync(socket, TimeSpan.FromSeconds(10));
        if (location is null)
        {
            Assert.Ignore("Bot isn't installed in any guild with a text channel - install it somewhere first to run this check.");
            return;
        }

        var commandName = $"e2e-ping-{Guid.NewGuid():N}"[..20];
        await RegisterGlobalCommandAsync(baseUrl, clientId, clientSecret, commandName, "E2E test command");

        var jwt = await GetBotJwtAsync(baseUrl, clientId, clientSecret);
        await InvokeCommandAsync(baseUrl, jwt, location.Value.GuildId, location.Value.ChannelId, clientId, commandName);

        var interactionCreate = await FindDispatchAsync(socket, "INTERACTION_CREATE", TimeSpan.FromSeconds(15));
        Assert.That(interactionCreate, Is.Not.Null, "never received an INTERACTION_CREATE dispatch for the command we just invoked");

        var interaction = interactionCreate!.D!.Value.Deserialize<InteractionPayload>()!;
        Assert.Multiple(() =>
        {
            Assert.That(interaction.Data.Name, Is.EqualTo(commandName));
            Assert.That(interaction.ChannelId, Is.EqualTo(location.Value.ChannelId));
            Assert.That(interaction.GuildId, Is.EqualTo(location.Value.GuildId));
        });

        var marker = $"e2e-reply-{Guid.NewGuid():N}";
        await PostInteractionCallbackAsync(baseUrl, interaction.Id, interaction.Token, type: 4, content: marker);

        var messageCreate = await FindDispatchAsync(socket, "MESSAGE_CREATE", TimeSpan.FromSeconds(15));
        Assert.That(messageCreate, Is.Not.Null, "never received a MESSAGE_CREATE dispatch for the interaction response");

        var message = messageCreate!.D!.Value.Deserialize<MessageCreatePayload>()!;
        Assert.Multiple(() =>
        {
            Assert.That(message.Content, Is.EqualTo(marker));
            Assert.That(message.ChannelId, Is.EqualTo(location.Value.ChannelId));
            Assert.That(message.Author.Id, Is.EqualTo(clientId));
        });

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", closeCts.Token);
    }

    /// <summary>Same round trip, but the bot defers (type 5) first and posts the real response
    /// later via the followup webhook endpoint - proves the flow real-world commands that do
    /// actual work (can't answer within Discord's 3s window) depend on.</summary>
    [Test]
    public async Task RegisterCommand_Invoke_DeferThenFollowUp_CreatesMessage()
    {
        var (clientId, clientSecret, baseUrl) = LocalE2ECredentials.Load();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            Assert.Ignore("Set BOTS_E2E_CLIENT_ID/BOTS_E2E_CLIENT_SECRET or populate Bots.Tests/.e2e-credentials.local.json to run this against a live deployment.");
            return;
        }

        using var socket = new ClientWebSocket();
        await ConnectAndIdentifyAsync(socket, baseUrl, clientId, clientSecret);

        var location = await FindTextChannelFromGuildCreatesAsync(socket, TimeSpan.FromSeconds(10));
        if (location is null)
        {
            Assert.Ignore("Bot isn't installed in any guild with a text channel - install it somewhere first to run this check.");
            return;
        }

        var commandName = $"e2e-defer-{Guid.NewGuid():N}"[..20];
        await RegisterGlobalCommandAsync(baseUrl, clientId, clientSecret, commandName, "E2E deferred test command");

        var jwt = await GetBotJwtAsync(baseUrl, clientId, clientSecret);
        await InvokeCommandAsync(baseUrl, jwt, location.Value.GuildId, location.Value.ChannelId, clientId, commandName);

        var interactionCreate = await FindDispatchAsync(socket, "INTERACTION_CREATE", TimeSpan.FromSeconds(15));
        Assert.That(interactionCreate, Is.Not.Null, "never received an INTERACTION_CREATE dispatch for the command we just invoked");
        var interaction = interactionCreate!.D!.Value.Deserialize<InteractionPayload>()!;

        await PostInteractionCallbackAsync(baseUrl, interaction.Id, interaction.Token, type: 5, content: null);

        var marker = $"e2e-followup-{Guid.NewGuid():N}";
        await PostFollowupAsync(baseUrl, clientId, interaction.Token, marker);

        var messageCreate = await FindDispatchAsync(socket, "MESSAGE_CREATE", TimeSpan.FromSeconds(15));
        Assert.That(messageCreate, Is.Not.Null, "never received a MESSAGE_CREATE dispatch for the deferred follow-up");

        var message = messageCreate!.D!.Value.Deserialize<MessageCreatePayload>()!;
        Assert.That(message.Content, Is.EqualTo(marker));

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", closeCts.Token);
    }

    private static async Task<(ReadyPayload Ready, string GatewayUrl)> ConnectAndIdentifyAsync(
        ClientWebSocket socket, string baseUrl, string clientId, string clientSecret)
    {
        var gatewayUrl = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/api/discord/v10/gateway";
        var compatToken = DiscordCompatToken.Pack(clientId, clientSecret);

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

        return (ready.D!.Value.Deserialize<ReadyPayload>()!, gatewayUrl);
    }

    /// <summary>Drains dispatches for up to <paramref name="timeout"/> looking for the first
    /// GUILD_CREATE that has at least one text channel, returning its guild + channel id.</summary>
    private static async Task<(string GuildId, string ChannelId)?> FindTextChannelFromGuildCreatesAsync(ClientWebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var envelope = await ReceiveEnvelopeAsync(socket, timeout);
                if (envelope.T != "GUILD_CREATE") continue;

                var guild = envelope.D!.Value.Deserialize<GuildCreatePayload>()!;
                var textChannel = guild.Channels.FirstOrDefault(c => c.Type == DiscordChannelType.GuildText);
                if (textChannel is not null) return (guild.Id, textChannel.Id);
            }
        }
        catch (OperationCanceledException)
        {
            // no GUILD_CREATE with a text channel arrived in time - caller treats null as "skip"
        }

        return null;
    }

    /// <summary>Drains dispatches for up to <paramref name="timeout"/> looking for the named
    /// event, ignoring anything else (heartbeat acks, other dispatches) in between.</summary>
    private static async Task<GatewayEnvelope?> FindDispatchAsync(ClientWebSocket socket, string eventName, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var envelope = await ReceiveEnvelopeAsync(socket, timeout);
                if (envelope.T == eventName) return envelope;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return null;
    }

    private static async Task PostMessageAsync(string baseUrl, string clientId, string clientSecret, string channelId, string content)
    {
        using var http = new HttpClient();
        var compatToken = DiscordCompatToken.Pack(clientId, clientSecret);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", compatToken);

        var response = await http.PostAsJsonAsync($"{baseUrl}/api/discord/v10/channels/{channelId}/messages", new { content });
        response.EnsureSuccessStatusCode();
    }

    private static async Task RegisterGlobalCommandAsync(string baseUrl, string clientId, string clientSecret, string name, string description)
    {
        using var http = new HttpClient();
        var compatToken = DiscordCompatToken.Pack(clientId, clientSecret);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", compatToken);

        var request = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/discord/v10/applications/{clientId}/commands")
        {
            Content = JsonContent.Create(new[] { new { name, description } }),
        };
        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Fetches a real JWT for the bot, the same client_credentials exchange
    /// BotTokenTranslator does server-side - needed because the native invoke endpoint is a
    /// normal venta-JWT-authed endpoint, not a Discord-compat one, so the packed "Bot" token
    /// doesn't apply there.</summary>
    private static async Task<string> GetBotJwtAsync(string baseUrl, string clientId, string clientSecret)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"{baseUrl}/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        }));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>Invokes a command using the bot's own JWT to stand in for "a human invoked this" -
    /// the native endpoint just checks the caller has SendMessages in the channel, which the bot
    /// itself does once installed, so this exercises the exact same dispatch path a real human
    /// invocation would without needing a separate human test account.</summary>
    private static async Task InvokeCommandAsync(string baseUrl, string jwt, string guildId, string channelId, string botUserId, string commandName)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await http.PostAsJsonAsync(
            $"{baseUrl}/api/v1/bots/guilds/{guildId}/channels/{channelId}/interactions",
            new { botUserId, commandName, options = Array.Empty<object>() });
        response.EnsureSuccessStatusCode();
    }

    private static async Task PostInteractionCallbackAsync(string baseUrl, string interactionId, string token, int type, string? content)
    {
        using var http = new HttpClient();
        object body = content is null
            ? new { type }
            : new { type, data = new { content, flags = 0 } };

        var response = await http.PostAsJsonAsync($"{baseUrl}/api/discord/v10/interactions/{interactionId}/{token}/callback", body);
        response.EnsureSuccessStatusCode();
    }

    private static async Task PostFollowupAsync(string baseUrl, string applicationId, string token, string content)
    {
        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync($"{baseUrl}/api/discord/v10/webhooks/{applicationId}/{token}", new { content, flags = 0 });
        response.EnsureSuccessStatusCode();
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
