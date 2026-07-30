using Bots.Application.Gateway;
using Bots.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Bots.Tests.Gateway;

/// <summary>
/// Covers the parts of GatewayConnectionRegistry not already exercised indirectly through
/// Gateway/Handlers/*.cs tests (which only ever call PublishAsync): the local connection
/// bookkeeping (Add/value-comparing Remove/LocalConnections) and the Start() Redis pub/sub
/// fan-out callback itself, driven directly through FakeSubscriber.SubscribedHandler.
/// </summary>
[TestFixture]
public class GatewayConnectionRegistryTests
{
    private static readonly IServiceScopeFactory DummyScopeFactory =
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private static GatewayConnection MakeConnection(FakeWebSocket socket, GatewayConnectionRegistry registry) =>
        new(socket, DummyScopeFactory, registry, NullLogger<GatewayConnection>.Instance);

    [Test]
    public void Add_ThenLocalConnections_ContainsIt()
    {
        var (registry, _) = GatewayRegistryTestFactory.Create();
        var connection = MakeConnection(new FakeWebSocket(), registry);

        registry.Add("usr_bot1", connection);

        Assert.That(registry.LocalConnections, Is.EquivalentTo(new[] { connection }));
    }

    [Test]
    public void Remove_SameConnectionInstance_RemovesIt()
    {
        var (registry, _) = GatewayRegistryTestFactory.Create();
        var connection = MakeConnection(new FakeWebSocket(), registry);
        registry.Add("usr_bot1", connection);

        registry.Remove("usr_bot1", connection);

        Assert.That(registry.LocalConnections, Is.Empty);
    }

    [Test]
    public void Remove_DifferentConnectionInstanceUnderSameKey_DoesNotEvictTheNewerOne()
    {
        // Guards a specific race: an old connection's teardown must not evict a newer connection
        // that already replaced it under the same bot user id.
        var (registry, _) = GatewayRegistryTestFactory.Create();
        var oldConnection = MakeConnection(new FakeWebSocket(), registry);
        var newConnection = MakeConnection(new FakeWebSocket(), registry);
        registry.Add("usr_bot1", newConnection);

        registry.Remove("usr_bot1", oldConnection);

        Assert.That(registry.LocalConnections, Is.EquivalentTo(new[] { newConnection }));
    }

    [Test]
    public async Task PublishAsync_PublishesJsonEnvelopeToDispatchChannel()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        await registry.PublishAsync("usr_bot1", "MESSAGE_CREATE", new { content = "hi" });

        var (botUserId, eventName, data) = DispatchAssertions.Parse(subscriber.Messages.Single());
        Assert.Multiple(() =>
        {
            Assert.That(botUserId, Is.EqualTo("usr_bot1"));
            Assert.That(eventName, Is.EqualTo("MESSAGE_CREATE"));
            Assert.That(data.GetProperty("content").GetString(), Is.EqualTo("hi"));
        });
    }

    [Test]
    public void Start_SubscribesToTheSharedDispatchChannel()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();

        registry.Start();

        Assert.That(subscriber.SubscribedHandler, Is.Not.Null);
    }

    [Test]
    public async Task Start_MessageForALocallyHeldConnection_DeliversTheDispatch()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        registry.Start();
        var socket = new FakeWebSocket();
        var connection = MakeConnection(socket, registry);
        registry.Add("usr_bot1", connection);

        await registry.PublishAsync("usr_bot1", "MESSAGE_CREATE", new { content = "hi" });
        var published = subscriber.Messages.Single();
        subscriber.SubscribedHandler!(published.Channel, published.Message);

        // the async-void handler's own work is all synchronously-completable (in-memory fake
        // socket, no real I/O) so it's done by the time Subscribe's Action returns.
        Assert.That(socket.SentTextMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Start_MessageForABotUserIdHeldOnAnotherPod_IsSilentlyIgnored()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        registry.Start();
        await registry.PublishAsync("usr_bot_on_another_pod", "MESSAGE_CREATE", new { content = "hi" });
        var published = subscriber.Messages.Single();

        Assert.DoesNotThrow(() => subscriber.SubscribedHandler!(published.Channel, published.Message));
    }

    [Test]
    public void Start_MalformedJsonMessage_IsCaughtAndDoesNotThrow()
    {
        var (registry, subscriber) = GatewayRegistryTestFactory.Create();
        registry.Start();

        Assert.DoesNotThrow(() => subscriber.SubscribedHandler!(RedisChannel.Literal("bots-gateway:dispatch"), "not-valid-json"));
    }
}
