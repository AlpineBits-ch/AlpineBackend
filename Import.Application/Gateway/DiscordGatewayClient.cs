using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AppEnvironment;
using Bots.Contracts.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Import.Application.Discord;

namespace Import.Application.Gateway;

/// <summary>
/// Maintains ONE persistent outbound WebSocket connection to Discord's real Gateway using the
/// Echo-owned import bot's token - the reverse of
/// Bots.Application/Gateway/GatewayWebSocketMiddleware.cs (that accepts inbound connections FROM
/// Discord bot libraries; here Echo itself is the client speaking to real discord.com).
/// </summary>
public class DiscordGatewayClient(
    IServiceScopeFactory scopeFactory,
    ILogger<DiscordGatewayClient> logger) : BackgroundService
{
    private const long GuildsIntent = 1 << 0;
    private const string GatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";

    private string? _sessionId;
    private int _lastSequence;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(Env.DiscordImport.BotToken))
        {
            logger.LogWarning("DISCORD_IMPORT_BOT_TOKEN is not configured - Discord live-sync Gateway client will not start");
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSingleConnectionAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Discord Gateway connection dropped; reconnecting in {Delay}s", backoff.TotalSeconds);
            }

            try
            {
                await Task.Delay(backoff, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }
    }

    private async Task RunSingleConnectionAsync(CancellationToken stoppingToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(GatewayUrl), stoppingToken);

        var buffer = new byte[32 * 1024];
        var hello = await ReceiveEnvelopeAsync(socket, buffer, stoppingToken);
        if (hello is null || hello.Op != (int)GatewayOpCode.Hello)
        {
            throw new InvalidOperationException("Expected HELLO as the first Gateway frame");
        }

        var heartbeatInterval = hello.D?.Deserialize<HelloPayload>()?.HeartbeatInterval ?? 41_250;

        if (_sessionId is not null)
        {
            await SendOpAsync(socket, GatewayOpCode.Resume, new ResumePayload
            {
                Token = Env.DiscordImport.BotToken,
                SessionId = _sessionId,
                Seq = _lastSequence,
            }, stoppingToken);
        }
        else
        {
            await SendIdentifyAsync(socket, stoppingToken);
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeatLoop = HeartbeatLoopAsync(socket, heartbeatInterval, heartbeatCts.Token);

        try
        {
            await ReceiveLoopAsync(socket, buffer, stoppingToken);
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatLoop; } catch (OperationCanceledException) { }
        }
    }

    private async Task SendIdentifyAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var properties = JsonSerializer.SerializeToElement(new { os = "linux", browser = "venta-import", device = "venta-import" });
        await SendOpAsync(socket, GatewayOpCode.Identify, new IdentifyPayload
        {
            Token = Env.DiscordImport.BotToken,
            Intents = GuildsIntent,
            Properties = properties,
        }, ct);
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, int intervalMs, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(intervalMs, ct);
                await SendOpAsync(socket, GatewayOpCode.Heartbeat, _lastSequence == 0 ? (int?)null : _lastSequence, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // connection ending
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var envelope = await ReceiveEnvelopeAsync(socket, buffer, ct);
            if (envelope is null) return;

            if (envelope.S.HasValue) _lastSequence = envelope.S.Value;

            switch ((GatewayOpCode)envelope.Op)
            {
                case GatewayOpCode.Dispatch:
                    await HandleDispatchAsync(envelope, ct);
                    break;

                case GatewayOpCode.HeartbeatAck:
                    break;

                case GatewayOpCode.Reconnect:
                    logger.LogInformation("Discord Gateway requested a reconnect (OP 7)");
                    return;

                case GatewayOpCode.InvalidSession:
                    // Non-resumable - the well-known case for a session that's simply too old.
                    logger.LogWarning("Discord Gateway sent Invalid Session - reconnecting fresh");
                    _sessionId = null;
                    return;

                default:
                    break;
            }
        }
    }

    private async Task HandleDispatchAsync(GatewayEnvelope envelope, CancellationToken ct)
    {
        if (envelope.T == "READY")
        {
            _sessionId = envelope.D?.Deserialize<ReadyPayload>()?.SessionId;
            logger.LogInformation("Discord Gateway READY (session {SessionId})", _sessionId);
            return;
        }

        if (envelope.T == "RESUMED")
        {
            logger.LogInformation("Discord Gateway session resumed");
            return;
        }

        if (envelope.D is null) return;

        using var scope = scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<DiscordStructureSyncHandler>();

        try
        {
            switch (envelope.T)
            {
                case "CHANNEL_CREATE":
                case "CHANNEL_UPDATE":
                    await handler.HandleChannelUpsertAsync(envelope.D.Value.Deserialize<DiscordChannelPayload>()!, ct);
                    break;

                case "CHANNEL_DELETE":
                    await handler.HandleChannelDeleteAsync(envelope.D.Value.Deserialize<DiscordChannelPayload>()!, ct);
                    break;

                case "GUILD_ROLE_CREATE":
                case "GUILD_ROLE_UPDATE":
                {
                    var payload = envelope.D.Value.Deserialize<DiscordRoleDispatchPayload>()!;
                    await handler.HandleRoleUpsertAsync(payload.GuildId, payload.Role, ct);
                    break;
                }

                case "GUILD_ROLE_DELETE":
                {
                    var payload = envelope.D.Value.Deserialize<DiscordRoleDeletePayload>()!;
                    await handler.HandleRoleDeleteAsync(payload.GuildId, payload.RoleId, ct);
                    break;
                }

                default:
                    // GUILD_CREATE and everything else (presence/typing/voice/etc.) is ignored -
                    // structure sync only cares about channel/role CRUD.
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply Discord dispatch {EventType}", envelope.T);
        }
    }

    private static async Task<GatewayEnvelope?> ReceiveEnvelopeAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        if (stream.Length == 0) return null;
        stream.Seek(0, SeekOrigin.Begin);
        return await JsonSerializer.DeserializeAsync<GatewayEnvelope>(stream, cancellationToken: ct);
    }

    private static async Task SendOpAsync<T>(ClientWebSocket socket, GatewayOpCode op, T? payload, CancellationToken ct)
    {
        var envelope = new GatewayOutboundEnvelope<T> { Op = (int)op, D = payload };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
