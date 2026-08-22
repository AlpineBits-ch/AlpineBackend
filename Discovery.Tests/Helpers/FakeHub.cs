using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Discovery.Tests.Helpers;

/// <summary>Hand-rolled no-op IHubContext&lt;EchoRealtimeHub&gt; for any suite exercising
/// ListingRealtime - Discovery.Tests has no other one (unlike Guild.Tests/Messaging.Tests).</summary>
public sealed class FakeHub : IHubContext<EchoRealtimeHub>
{
    public List<(string Method, IReadOnlyList<string> UserIds)> Sent { get; } = [];

    public IHubClients Clients { get; }
    public IGroupManager Groups => throw new NotSupportedException();

    public FakeHub() => Clients = new FakeHubClients(this);

    private sealed class FakeHubClients(FakeHub owner) : IHubClients
    {
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => new FakeClientProxy(owner, [userId]);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new FakeClientProxy(owner, userIds);
    }

    private sealed class FakeClientProxy(FakeHub owner, IReadOnlyList<string> userIds) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            owner.Sent.Add((method, userIds));
            return Task.CompletedTask;
        }
    }
}
