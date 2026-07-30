using NSubstitute;
using StackExchange.Redis;

namespace Guild.Tests.Helpers;

/// <summary>
/// Builds a fake <see cref="IConnectionMultiplexer"/> for constructing a real
/// <c>GuildHydrateService</c> in unit tests.
/// </summary>
internal static class RedisTestFactory
{
    public static IConnectionMultiplexer Create(out IDatabase database)
    {
        database = Substitute.For<IDatabase>();
        database
            .SortedSetRangeByScoreAsync(
                Arg.Any<RedisKey>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
                Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<RedisValue>()));

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return multiplexer;
    }

    public static IConnectionMultiplexer Create() => Create(out _);
}
