using System.Net;
using StackExchange.Redis;
using StackExchange.Redis.Maintenance;
using StackExchange.Redis.Profiling;

namespace Bots.Tests.Helpers;

/// <summary>Hand-rolled fake IConnectionMultiplexer/ISubscriber pair so GatewayConnectionRegistry
/// (and the Gateway/Handlers/*.cs classes built on top of it) can be constructed and exercised in
/// tests without a real Redis instance - mirrors this repo's no-mocking-framework convention.
/// Only ISubscriber.PublishAsync (the one member GatewayConnectionRegistry.PublishAsync actually
/// calls) does anything; everything else throws since nothing in this suite should reach it.</summary>
internal sealed class FakeSubscriber : ISubscriber
{
    public record Published(RedisChannel Channel, RedisValue Message);
    public List<Published> Messages { get; } = new();

    public long Publish(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None)
    {
        Messages.Add(new Published(channel, message));
        return 1;
    }

    public Task<long> PublishAsync(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None)
    {
        Messages.Add(new Published(channel, message));
        return Task.FromResult(1L);
    }

    public IConnectionMultiplexer Multiplexer => throw new NotImplementedException();
    public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public ChannelMessageQueue Subscribe(RedisChannel channel, CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public Task SubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public Task<ChannelMessageQueue> SubscribeAsync(RedisChannel channel, CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public EndPoint? IdentifyEndpoint(RedisChannel channel = default, CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public Task<EndPoint?> IdentifyEndpointAsync(RedisChannel channel = default, CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public bool IsConnected(RedisChannel channel = default) => true;
    public EndPoint? SubscribedEndpoint(RedisChannel channel) => throw new NotImplementedException();
    public void Unsubscribe(RedisChannel channel, Action<RedisChannel, RedisValue>? handler = null, CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public void UnsubscribeAll(CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public Task UnsubscribeAllAsync(CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public Task UnsubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue>? handler = null, CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public TimeSpan Ping(CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public bool TryWait(Task task) => throw new NotImplementedException();
    public void Wait(Task task) => throw new NotImplementedException();
    public T Wait<T>(Task<T> task) => throw new NotImplementedException();
    public void WaitAll(params Task[] tasks) => throw new NotImplementedException();
}

internal sealed class FakeRedisMultiplexer(ISubscriber subscriber) : IConnectionMultiplexer
{
    public ISubscriber GetSubscriber(object? asyncState = null) => subscriber;

    // Nothing below this point is exercised by GatewayConnectionRegistry/the Gateway handlers.
    public string ClientName => throw new NotImplementedException();
    public string Configuration => throw new NotImplementedException();
    public int TimeoutMilliseconds => throw new NotImplementedException();
    public long OperationCount => throw new NotImplementedException();
    public bool PreserveAsyncOrder { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public bool IsConnected => true;
    public bool IsConnecting => false;
    public bool IncludeDetailInExceptions { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int StormLogThreshold { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public IBatch CreateBatch(object? asyncState = null) => throw new NotImplementedException();
    public ITransaction CreateTransaction(object? asyncState = null) => throw new NotImplementedException();
    public IDatabase GetDatabase(int db = -1, object? asyncState = null) => throw new NotImplementedException();
    public IServer GetServer(string host, int port, object? asyncState = null) => throw new NotImplementedException();
    public IServer GetServer(string hostAndPort, object? asyncState = null) => throw new NotImplementedException();
    public IServer GetServer(IPAddress host, int port) => throw new NotImplementedException();
    public IServer GetServer(EndPoint endpoint, object? asyncState = null) => throw new NotImplementedException();
    public IServer[] GetServers() => throw new NotImplementedException();
    public string GetStatus() => throw new NotImplementedException();
    public void GetStatus(TextWriter log) => throw new NotImplementedException();
    public void Close(bool allowCommandsToComplete = true) => throw new NotImplementedException();
    public Task CloseAsync(bool allowCommandsToComplete = true) => throw new NotImplementedException();
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public string? GetStormLog() => throw new NotImplementedException();
    public void ResetStormLog() => throw new NotImplementedException();
    public int GetHashSlot(RedisKey key) => throw new NotImplementedException();
    public void ExportConfiguration(Stream destination, ExportOptions options = ExportOptions.All) => throw new NotImplementedException();
    public void RegisterProfiler(Func<ProfilingSession?> profilingSessionProvider) => throw new NotImplementedException();
    public ServerCounters GetCounters() => throw new NotImplementedException();
    public EndPoint[] GetEndPoints(bool configuredOnly = false) => throw new NotImplementedException();
    public void Wait(Task task) => throw new NotImplementedException();
    public T Wait<T>(Task<T> task) => throw new NotImplementedException();
    public void WaitAll(params Task[] tasks) => throw new NotImplementedException();
    public int HashSlot(RedisKey key) => throw new NotImplementedException();
    public Task<bool> ConfigureAsync(TextWriter? log = null) => throw new NotImplementedException();
    public bool Configure(TextWriter? log = null) => throw new NotImplementedException();
    public long PublishReconfigure(CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public Task<long> PublishReconfigureAsync(CommandFlags flags = CommandFlags.None) => throw new NotImplementedException();
    public void AddLibraryNameSuffix(string suffix) => throw new NotImplementedException();
    public event EventHandler<RedisErrorEventArgs>? ErrorMessage;
    public event EventHandler<ConnectionFailedEventArgs>? ConnectionFailed;
    public event EventHandler<InternalErrorEventArgs>? InternalError;
    public event EventHandler<ConnectionFailedEventArgs>? ConnectionRestored;
    public event EventHandler<EndPointEventArgs>? ConfigurationChanged;
    public event EventHandler<EndPointEventArgs>? ConfigurationChangedBroadcast;
    public event EventHandler<ServerMaintenanceEvent>? ServerMaintenanceEvent;
    public event EventHandler<HashSlotMovedEventArgs>? HashSlotMoved;
}
