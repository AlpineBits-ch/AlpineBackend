using NSubstitute;
using StackExchange.Redis;

namespace Isle.Tests.Helpers.Redis;

/// <summary>Builds a fake <see cref="IConnectionMultiplexer"/> whose <c>GetDatabase()</c> returns a fresh in-memory fake.</summary>
internal static class RedisTestFactory
{
    public static IConnectionMultiplexer Create(out FakeRedisStore store)
    {
        store = new FakeRedisStore();
        var database = new FakeRedisDatabase(store);

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        return multiplexer;
    }

    public static IConnectionMultiplexer Create() => Create(out _);
}
