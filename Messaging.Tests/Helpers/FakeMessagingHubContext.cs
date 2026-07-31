using Echo.Realtime;

using Microsoft.AspNetCore.SignalR;

namespace Messaging.Tests.Helpers;

public class FakeMessagingHubContext : IHubContext<EchoRealtimeHub>
{
    public IHubClients Clients { get; } = new FakeHubClients();
    public IGroupManager Groups { get; } = new FakeGroupManager();
}

/// <summary>Records not just what was pushed but who it was pushed to.</summary>
public class FakeHubClients : IHubClients
{
    /// <summary>Back-compat view for tests that only care what was sent.</summary>
    public List<(string Method, object[] Args)> SentMessages { get; } = new();

    /// <summary>Every send with its target, e.g. <c>group:device:user-2:device-b</c>,
    /// <c>users:user-1,user-2</c>, <c>user:user-1</c>.</summary>
    public List<(string Target, string Method, object[] Args)> Sends { get; } = new();

    private IClientProxy Proxy(string target) => new FakeClientProxy(target, SentMessages, Sends);

    public IClientProxy All => Proxy("all");
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy("all-except");
    public IClientProxy Client(string connectionId) => Proxy("client:" + connectionId);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy("clients:" + string.Join(",", connectionIds));
    public IClientProxy Group(string groupName) => Proxy("group:" + groupName);
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy("group-except:" + groupName);
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy("groups:" + string.Join(",", groupNames));
    public IClientProxy User(string userId) => Proxy("user:" + userId);
    public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy("users:" + string.Join(",", userIds));
}

public class FakeClientProxy : IClientProxy
{
    private readonly string _target;
    private readonly List<(string Method, object[] Args)> _log;
    private readonly List<(string Target, string Method, object[] Args)> _targeted;

    public FakeClientProxy(
        string target,
        List<(string, object[])> log,
        List<(string, string, object[])> targeted)
    {
        _target = target;
        _log = log;
        _targeted = targeted;
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
    {
        _log.Add((method, args!));
        _targeted.Add((_target, method, args!));
        return Task.CompletedTask;
    }
}

public class FakeGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
}
