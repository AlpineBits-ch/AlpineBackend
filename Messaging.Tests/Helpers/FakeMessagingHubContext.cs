using Echo.Realtime;

using Microsoft.AspNetCore.SignalR;

namespace Messaging.Tests.Helpers;

public class FakeMessagingHubContext : IHubContext<EchoRealtimeHub>
{
    public IHubClients Clients { get; } = new FakeHubClients();
    public IGroupManager Groups { get; } = new FakeGroupManager();
}

public class FakeHubClients : IHubClients
{
    public List<(string Method, object[] Args)> SentMessages { get; } = new();
    private IClientProxy _proxy;

    public FakeHubClients() => _proxy = new FakeClientProxy(SentMessages);

    public IClientProxy All => _proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
    public IClientProxy Client(string connectionId) => _proxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
    public IClientProxy Group(string groupName) => _proxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
    public IClientProxy User(string userId) => _proxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
}

public class FakeClientProxy : IClientProxy
{
    private readonly List<(string Method, object[] Args)> _log;
    public FakeClientProxy(List<(string, object[])> log) => _log = log;

    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
    {
        _log.Add((method, args!));
        return Task.CompletedTask;
    }
}

public class FakeGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
}