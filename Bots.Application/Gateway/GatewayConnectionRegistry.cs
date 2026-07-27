using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;

namespace Bots.Application.Gateway;

/// <summary>
/// Tracks Gateway WebSocket connections held by *this* pod, and fans dispatches out to whichever
/// pod actually holds a given bot's connection via a single shared Redis pub/sub channel.
/// IHubContext&lt;EchoRealtimeHub&gt;'s SignalR backplane can't be joined from here (it's
/// send-only), so this is Bots.Application's own equivalent - every pod subscribes to the same
/// channel and filters locally, the same broadcast-and-filter trade-off SignalR's own backplane
/// already makes for Clients.User(id).
/// </summary>
public class GatewayConnectionRegistry
{
    private const string DispatchChannel = "bots-gateway:dispatch";

    private readonly ConcurrentDictionary<string, GatewayConnection> _connections = new();
    private readonly ISubscriber _subscriber;
    private readonly ILogger<GatewayConnectionRegistry> _logger;

    public GatewayConnectionRegistry(IConnectionMultiplexer redis, ILogger<GatewayConnectionRegistry> logger)
    {
        _subscriber = redis.GetSubscriber();
        _logger = logger;
    }

    /// <summary>Call once at startup so this pod picks up dispatches meant for connections it holds.</summary>
    public void Start()
    {
        _subscriber.Subscribe(RedisChannel.Literal(DispatchChannel), async (_, message) =>
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<DispatchEnvelope>((string)message!);
                if (envelope is null) return;

                if (_connections.TryGetValue(envelope.BotUserId, out var connection))
                {
                    await connection.SendDispatchAsync(envelope.EventName, envelope.Data);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to process Gateway dispatch fan-out message");
            }
        });
    }

    public void Add(string botUserId, GatewayConnection connection) => _connections[botUserId] = connection;

    /// <summary>Value-comparing remove, so a connection being torn down never evicts a newer one
    /// that already replaced it under the same bot user id.</summary>
    public void Remove(string botUserId, GatewayConnection connection) =>
        ((ICollection<KeyValuePair<string, GatewayConnection>>)_connections).Remove(new(botUserId, connection));

    public async Task PublishAsync(string botUserId, string eventName, object data)
    {
        var envelope = new DispatchEnvelope(botUserId, eventName, JsonSerializer.SerializeToElement(data));
        await _subscriber.PublishAsync(RedisChannel.Literal(DispatchChannel), JsonSerializer.Serialize(envelope));
    }

    /// <summary>Connections held by this pod - used for graceful-drain OP 7 Reconnect fan-out.</summary>
    public IEnumerable<GatewayConnection> LocalConnections => _connections.Values;

    private record DispatchEnvelope(string BotUserId, string EventName, JsonElement Data);
}
