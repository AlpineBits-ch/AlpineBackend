using Bots.Application.Gateway;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bots.Tests.Helpers;

/// <summary>Builds a real GatewayConnectionRegistry backed by FakeRedisMultiplexer/FakeSubscriber
/// so Gateway/Handlers/*.cs classes (which call registry.PublishAsync) can be exercised without a
/// real Redis instance. Callers can inspect <see cref="Subscriber"/>.Messages to see exactly what
/// was fanned out - each captured message is the JSON-serialized DispatchEnvelope (BotUserId,
/// EventName, Data).</summary>
internal static class GatewayRegistryTestFactory
{
    public static (GatewayConnectionRegistry Registry, FakeSubscriber Subscriber) Create()
    {
        var subscriber = new FakeSubscriber();
        var multiplexer = new FakeRedisMultiplexer(subscriber);
        var registry = new GatewayConnectionRegistry(multiplexer, NullLogger<GatewayConnectionRegistry>.Instance);
        return (registry, subscriber);
    }
}
