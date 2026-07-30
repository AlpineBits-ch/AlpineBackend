using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Isle.Tests.Helpers;

/// <summary>Hand-rolled no-op IHubContext&lt;EchoRealtimeHub&gt; for handler unit tests that need to
/// satisfy a hub parameter - mirrors Guild.Tests/Helpers/FakeHubContext.cs and
/// Messaging.Tests/Helpers/FakeMessagingHubContext.cs (no mocking framework, per this repo's
/// convention). Records every SendAsync call for assertions, including which userId it targeted.</summary>
internal class FakeIsleHubContext : IHubContext<EchoRealtimeHub>
{
    public FakeIsleHubClients ClientsTyped { get; } = new();
    public IHubClients Clients => ClientsTyped;
    public IGroupManager Groups { get; } = new FakeIsleGroupManager();
}

internal class FakeIsleHubClients : IHubClients
{
    public List<(string? UserId, string Method, object?[] Args)> SentMessages { get; } = new();

    public IClientProxy All => new FakeIsleClientProxy(SentMessages, userId: null);
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
    public IClientProxy Client(string connectionId) => All;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
    public IClientProxy Group(string groupName) => All;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
    public IClientProxy User(string userId) => new FakeIsleClientProxy(SentMessages, userId);
    public IClientProxy Users(IReadOnlyList<string> userIds) => All;
}

internal class FakeIsleClientProxy(List<(string? UserId, string Method, object?[] Args)> log, string? userId) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
    {
        log.Add((userId, method, args));
        return Task.CompletedTask;
    }
}

internal class FakeIsleGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
}
