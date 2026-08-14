using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Echo.Tests.Entitlements;

/// <summary>Hand-rolled <c>IHubContext</c> for the change-notifier tests, following the repo's
/// no-mocking-framework convention. It records who each send was addressed to, because "a guild plan
/// change reaches that guild's members and a user plan change reaches nobody else" is the property
/// worth asserting.</summary>
public class FakeEntitlementsHubContext : IHubContext<EchoRealtimeHub>
{
    public FakeEntitlementsHubClients HubClients { get; } = new();

    public IHubClients Clients => HubClients;

    public IGroupManager Groups { get; } = new FakeEntitlementsGroupManager();
}

public class FakeEntitlementsHubClients : IHubClients
{
    /// <summary>Every send, with the users it was addressed to.</summary>
    public List<(IReadOnlyList<string> Recipients, string Method, object?[] Args)> Sent { get; } = [];

    private IClientProxy For(IReadOnlyList<string> recipients) =>
        new FakeEntitlementsClientProxy(Sent, recipients);

    public IClientProxy All => For([]);
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => For([]);
    public IClientProxy Client(string connectionId) => For([]);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => For([]);
    public IClientProxy Group(string groupName) => For([]);
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => For([]);
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => For([]);
    public IClientProxy User(string userId) => For([userId]);
    public IClientProxy Users(IReadOnlyList<string> userIds) => For(userIds);
}

public class FakeEntitlementsClientProxy(
    List<(IReadOnlyList<string> Recipients, string Method, object?[] Args)> log,
    IReadOnlyList<string> recipients) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
    {
        log.Add((recipients, method, args));
        return Task.CompletedTask;
    }
}

public class FakeEntitlementsGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) =>
        Task.CompletedTask;
}
