using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Bots.Tests.Helpers;

/// <summary>Hand-rolled IHubContext&lt;EchoRealtimeHub&gt; for the interaction tests - ephemeral
/// responses and modal opens are delivered over the hub rather than stored, so asserting on them
/// means capturing hub sends. Mirrors Guild.Tests/Helpers/FakeHubContext.cs (no mocking framework,
/// per this repo's convention), with the addition of recording which user each send targeted -
/// "only the invoker sees it" is the property these tests exist to prove.</summary>
public class FakeBotsHubContext : IHubContext<EchoRealtimeHub>
{
    public FakeBotsHubClients HubClients { get; } = new();

    public IHubClients Clients => HubClients;
    public IGroupManager Groups { get; } = new FakeBotsGroupManager();
}

public class FakeBotsHubClients : IHubClients
{
    /// <summary>Every send, with the user it was addressed to (null for a broadcast).</summary>
    public List<(string? UserId, string Method, object?[] Args)> Sent { get; } = new();

    private IClientProxy For(string? userId) => new FakeBotsClientProxy(Sent, userId);

    public IClientProxy All => For(null);
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => For(null);
    public IClientProxy Client(string connectionId) => For(null);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => For(null);
    public IClientProxy Group(string groupName) => For(null);
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => For(null);
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => For(null);
    public IClientProxy User(string userId) => For(userId);
    public IClientProxy Users(IReadOnlyList<string> userIds) => For(userIds.FirstOrDefault());
}

public class FakeBotsClientProxy(List<(string? UserId, string Method, object?[] Args)> log, string? userId) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
    {
        log.Add((userId, method, args));
        return Task.CompletedTask;
    }
}

public class FakeBotsGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
}
