using NSubstitute;
using StackExchange.Redis;

namespace Guild.Tests.Helpers;

/// <summary>
/// Builds a fake <see cref="IConnectionMultiplexer"/> for constructing a real
/// <c>GuildHydrateService</c> in unit tests. StackExchange.Redis's IDatabase/IConnectionMultiplexer
/// surfaces are far too large to hand-roll (unlike IMessageBus/IHubContext elsewhere in this test
/// project) so this uses NSubstitute, mirroring Isle.Tests/Helpers/Redis/RedisTestFactory.cs.
///
/// By default the fake database reports no online members (empty presence ZSET), which is enough
/// for endpoint tests that only need GuildHydrateService.GetGuildPresenceAsync to not throw - most
/// callers don't care about the actual presence set, just that hub broadcast fan-out has *someone*
/// (possibly nobody) to iterate over.
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
