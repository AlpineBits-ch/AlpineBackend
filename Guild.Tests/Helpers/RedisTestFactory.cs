using System.Text.Json;
using Guild.Application.Services;
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

    /// <summary>
    /// A fake whose presence ZSET reports <paramref name="present"/> as online, wired so
    /// <c>GuildHydrateService.GetGuildPresenceAsync</c> returns exactly those states.
    /// </summary>
    public static IConnectionMultiplexer CreateWithPresence(params MemberPresenceState[] present)
    {
        var database = Substitute.For<IDatabase>();

        database
            .SortedSetRangeByScoreAsync(
                Arg.Any<RedisKey>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
                Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(present.Select(p => (RedisValue)p.MemberId).ToArray()));

        // GetGuildPresenceAsync pipelines the per-member hash reads through a batch, so the batch
        // has to answer too - returning the state JSON the service deserializes back out.
        var batch = Substitute.For<IBatch>();
        foreach (var state in present)
        {
            var entries = new[] { new HashEntry("state", JsonSerializer.Serialize(state)) };
            batch.HashGetAllAsync($"presence:user:{state.MemberId}", Arg.Any<CommandFlags>())
                .Returns(Task.FromResult(entries));
        }

        database.CreateBatch(Arg.Any<object?>()).Returns(batch);

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return multiplexer;
    }
}
