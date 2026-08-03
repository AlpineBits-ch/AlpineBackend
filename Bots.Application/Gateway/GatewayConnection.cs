using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bots.Contracts.Gateway;
using Bots.Contracts.Gateway.Payloads;
using Bots.Domain.Entity;
using Ids;

namespace Bots.Application.Gateway;

/// <summary>
/// Owns one accepted Gateway WebSocket end-to-end: HELLO, waiting for IDENTIFY, the
/// READY/GUILD_CREATE burst, ongoing heartbeat tracking, and dispatch sends. Resume (OP 6) is
/// deliberately not supported - every attempt gets OP 9 Invalid Session (non-resumable), which
/// every real client library already treats as "reconnect cleanly and re-Identify," matching
/// what real Discord itself does whenever it can't honor a resume.
/// </summary>
public class GatewayConnection
{
    private const int HeartbeatIntervalMs = 41_250;
    private static readonly TimeSpan IdentifyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMilliseconds(HeartbeatIntervalMs * 2);

    private readonly WebSocket _socket;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GatewayConnectionRegistry _registry;
    private readonly ILogger<GatewayConnection> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private GatewaySession? _session;
    private DateTimeOffset _lastClientHeartbeat = DateTimeOffset.UtcNow;
    private int _sequence;

    public GatewayConnection(WebSocket socket, IServiceScopeFactory scopeFactory, GatewayConnectionRegistry registry, ILogger<GatewayConnection> logger)
    {
        _socket = socket;
        _scopeFactory = scopeFactory;
        _registry = registry;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken serverShutdown)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverShutdown);

        try
        {
            await SendOpAsync(GatewayOpCode.Hello, new HelloPayload { HeartbeatInterval = HeartbeatIntervalMs }, lifetime.Token);

            var authenticated = await WaitForIdentifyAsync(lifetime.Token);
            if (!authenticated) return;

            _registry.Add(_session!.BotUserId, this);

            var heartbeatMonitor = MonitorHeartbeatAsync(lifetime.Token);
            var receiveLoop = ReceiveLoopAsync(lifetime.Token);
            await Task.WhenAny(heartbeatMonitor, receiveLoop);
        }
        catch (WebSocketException)
        {
            // client dropped without a clean close frame - nothing more to do
        }
        catch (OperationCanceledException)
        {
            // server shutting down - the ApplicationStopping hook already sent OP 7 Reconnect
        }
        catch (Exception ex)
        {
            // Anything else (DI resolution failure, a bug in the handshake/hydration path, etc.)
            // would otherwise silently abort the raw socket with no trace - log it so a bad
            // connection is actually diagnosable instead of just showing up as a client-side
            // "closed without completing the close handshake".
            _logger.LogError(ex, "Unhandled error in Gateway connection (session {SessionId})", _session?.SessionId);
        }
        finally
        {
            await lifetime.CancelAsync();
            if (_session is not null) _registry.Remove(_session.BotUserId, this);
        }
    }

    private async Task<bool> WaitForIdentifyAsync(CancellationToken token)
    {
        using var timeoutCts = new CancellationTokenSource(IdentifyTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

        var buffer = new byte[16 * 1024];
        GatewayEnvelope? envelope;
        try
        {
            envelope = await ReceiveEnvelopeAsync(buffer, linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await CloseAsync(4003, "Not authenticated", token);
            return false;
        }

        if (envelope is null || envelope.Op != (int)GatewayOpCode.Identify)
        {
            await CloseAsync(4003, "Not authenticated", token);
            return false;
        }

        var identify = envelope.D?.Deserialize<IdentifyPayload>();
        if (string.IsNullOrWhiteSpace(identify?.Token))
        {
            await CloseAsync(4003, "Not authenticated", token);
            return false;
        }

        using var scope = _scopeFactory.CreateScope();
        var translator = scope.ServiceProvider.GetRequiredService<BotTokenTranslator>();
        var auth = await translator.AuthenticateAsync(identify.Token);
        if (!auth.Success || auth.BotUserId is null)
        {
            await CloseAsync(4004, "Authentication failed", token);
            return false;
        }

        _session = new GatewaySession
        {
            SessionId = Identifier.New("gwss"),
            BotUserId = auth.BotUserId,
            Intents = identify.Intents,
        };
        _lastClientHeartbeat = DateTimeOffset.UtcNow;

        var handshakeService = scope.ServiceProvider.GetRequiredService<GatewayHandshakeService>();
        var hydrated = await handshakeService.SendReadyAndGuildsAsync(this, _session, token);
        if (!hydrated)
        {
            await CloseAsync(4004, "Authentication failed", token);
            _session = null;
            return false;
        }

        return true;
    }

    // Task.WhenAny in RunAsync never observes either task's result/exception, so both loops must
    // swallow their own expected-shutdown exceptions internally - otherwise a faulted-but-never-
    // awaited Task risks an UnobservedTaskException on finalization.
    private async Task MonitorHeartbeatAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);

                if (DateTimeOffset.UtcNow - _lastClientHeartbeat > HeartbeatTimeout)
                {
                    await CloseAsync(4009, "Session timed out", token);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // connection ending for another reason (client disconnect, server shutdown)
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var envelope = await ReceiveEnvelopeAsync(buffer, token);
                if (envelope is null) return; // client sent a close frame

                switch ((GatewayOpCode)envelope.Op)
                {
                    case GatewayOpCode.Heartbeat:
                        _lastClientHeartbeat = DateTimeOffset.UtcNow;
                        await SendOpAsync<object?>(GatewayOpCode.HeartbeatAck, null, token);
                        break;

                    case GatewayOpCode.Resume:
                        // Not supported (see class remarks) - tell the client to Identify fresh.
                        await SendOpAsync(GatewayOpCode.InvalidSession, false, token);
                        break;

                    default:
                        // Unhandled client opcode (e.g. Presence/Voice State Update) - ignored,
                        // matching real Discord's tolerance of updates it doesn't act on.
                        break;
                }
            }
        }
        catch (WebSocketException)
        {
            // client dropped without a clean close frame
        }
        catch (OperationCanceledException)
        {
            // server shutting down
        }
    }

    private async Task<GatewayEnvelope?> ReceiveEnvelopeAsync(byte[] buffer, CancellationToken token)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Must echo a close frame back - otherwise the underlying connection just gets
                // torn down without completing the four-way close handshake, which surfaces to
                // the client as "closed the WebSocket connection without completing the close
                // handshake" even though the client itself closed cleanly.
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", token);
                }
                catch
                {
                    // best-effort - the underlying connection may already be gone
                }
                return null;
            }
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        if (stream.Length == 0) return null;

        stream.Seek(0, SeekOrigin.Begin);
        return await JsonSerializer.DeserializeAsync<GatewayEnvelope>(stream, cancellationToken: token);
    }

    public async Task SendDispatchAsync<T>(string eventName, T payload, CancellationToken token = default)
    {
        if (_socket.State != WebSocketState.Open) return;
        var envelope = new GatewayOutboundEnvelope<T>
        {
            Op = (int)GatewayOpCode.Dispatch,
            T = eventName,
            S = Interlocked.Increment(ref _sequence),
            D = payload,
        };
        await SendEnvelopeAsync(envelope, token);
    }

    /// <summary>Used by <see cref="GatewayConnectionRegistry"/> when re-delivering a dispatch
    /// that arrived over Redis pub/sub - by that point the payload is already a JsonElement, not
    /// the original strongly-typed DTO.</summary>
    public Task SendDispatchAsync(string eventName, JsonElement payload, CancellationToken token = default) =>
        SendDispatchAsync<JsonElement>(eventName, payload, token);

    public Task SendReconnectAsync(CancellationToken token = default) =>
        SendOpAsync<object?>(GatewayOpCode.Reconnect, null, token);

    private Task SendOpAsync<T>(GatewayOpCode op, T? payload, CancellationToken token) =>
        SendEnvelopeAsync(new GatewayOutboundEnvelope<T> { Op = (int)op, D = payload }, token);

    private async Task SendEnvelopeAsync<T>(GatewayOutboundEnvelope<T> envelope, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));

        await _sendLock.WaitAsync(token);
        try
        {
            if (_socket.State != WebSocketState.Open) return;
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(int closeCode, string reason, CancellationToken token)
    {
        if (_socket.State != WebSocketState.Open) return;
        try
        {
            await _socket.CloseAsync((WebSocketCloseStatus)closeCode, reason, token);
        }
        catch
        {
            // best-effort - the client may have already dropped the underlying connection
        }
    }
}
