using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Social.Tests.Helpers;

/// <summary>
/// Hand-rolled IHubContext&lt;EchoRealtimeHub&gt; for the social.* relationship push tests (no
/// mocking framework, per this repo's convention - mirrors Guild.Tests/Helpers/FakeHubContext.cs).
///
/// Unlike that one this records the *recipient* of every send, because the whole point of the
/// social.* pushes is that both parties get the same event with different, recipient-oriented
/// payloads - a fake that collapses Clients.User(x) and Clients.User(y) onto one proxy can't tell
/// those apart.
/// </summary>
internal sealed class FakeSocialHubContext : IHubContext<EchoRealtimeHub>
{
    private readonly FakeSocialHubClients _clients = new();

    public IHubClients Clients => _clients;
    public IGroupManager Groups { get; } = new FakeSocialGroupManager();

    public List<SentPush> Sent => _clients.Sent;

    public IEnumerable<SentPush> To(string userId) => Sent.Where(s => s.Recipients.Contains(userId));
}

internal sealed record SentPush(IReadOnlyList<string> Recipients, string Method, object?[] Args)
{
    public T Payload<T>() => (T)Args[0]!;
}

internal sealed class FakeSocialHubClients : IHubClients
{
    public List<SentPush> Sent { get; } = new();

    /// <summary>Recipient list of "*" marks a broadcast that isn't user-targeted - none of the
    /// social.* pushes use those, so a test asserting on them would be asserting a bug.</summary>
    private IClientProxy Any(params string[] recipients) => new FakeSocialClientProxy(Sent, recipients);

    public IClientProxy All => Any("*");
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Any("*");
    public IClientProxy Client(string connectionId) => Any(connectionId);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Any(connectionIds.ToArray());
    public IClientProxy Group(string groupName) => Any(groupName);
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Any(groupName);
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => Any(groupNames.ToArray());
    public IClientProxy User(string userId) => Any(userId);
    public IClientProxy Users(IReadOnlyList<string> userIds) => Any(userIds.ToArray());
}

internal sealed class FakeSocialClientProxy(List<SentPush> log, IReadOnlyList<string> recipients) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
    {
        log.Add(new SentPush(recipients, method, args));
        return Task.CompletedTask;
    }
}

internal sealed class FakeSocialGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
}
