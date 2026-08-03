using System.Text.Json;
using Guild.Application.Services;
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

    /// <summary>
    /// A fake whose presence ZSET reports <paramref name="present"/> as online, wired so
    /// <c>GuildHydrateService.GetGuildPresenceAsync</c> returns exactly those states.
    ///
    /// Needed by anything that cares *who* is present rather than just that the call succeeds -
    /// @here resolution, for one, where the difference between an Online and an Idle member decides
    /// whether they are mentioned at all.
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
