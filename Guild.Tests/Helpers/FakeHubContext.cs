using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Guild.Tests.Helpers;

/// <summary>Hand-rolled no-op IHubContext&lt;EchoRealtimeHub&gt; for handler unit tests that need
/// to satisfy a hub parameter - mirrors Messaging.Tests/Helpers/FakeMessagingHubContext.cs (no
/// mocking framework, per this repo's convention). Records every SendAsync call for assertions.</summary>
public class FakeHubContext : IHubContext<EchoRealtimeHub>
{
    public IHubClients Clients { get; } = new FakeHubClients();
    public IGroupManager Groups { get; } = new FakeGroupManager();
}

public class FakeHubClients : IHubClients
{
    public List<(string Method, object?[] Args)> SentMessages { get; } = new();

    /// <summary>The same sends, but paired with the user ids they were addressed to.</summary>
    public List<(string Method, IReadOnlyList<string> UserIds, object?[] Args)> SentToUsers { get; } = new();

    private readonly IClientProxy _proxy;

    public FakeHubClients() => _proxy = new FakeClientProxy(this, []);

    private IClientProxy ProxyFor(IEnumerable<string> userIds) =>
        new FakeClientProxy(this, userIds.ToList());

    public IClientProxy All => _proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
    public IClientProxy Client(string connectionId) => _proxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
    public IClientProxy Group(string groupName) => _proxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
    public IClientProxy User(string userId) => ProxyFor([userId]);
    public IClientProxy Users(IReadOnlyList<string> userIds) => ProxyFor(userIds);

    /// <summary>Every user id addressed by a send of <paramref name="method"/>, deduplicated.</summary>
    public List<string> RecipientsOf(string method) =>
        SentToUsers.Where(s => s.Method == method)
            .SelectMany(s => s.UserIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

public class FakeClientProxy(FakeHubClients owner, IReadOnlyList<string> userIds) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
    {
        owner.SentMessages.Add((method, args));
        owner.SentToUsers.Add((method, userIds, args));
        return Task.CompletedTask;
    }
}

public class FakeGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
}
