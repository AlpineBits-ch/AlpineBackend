using Isle.Tests.Helpers.Redis;
using StackExchange.Redis;

namespace Isle.Tests.Tests.KingOfTheHill;

/// <summary>
/// Sanity checks for the fake <see cref="IDatabase"/> itself, independent of any real ledger - if
/// these ever fail, every ledger test's failure is noise until this is fixed first.
/// </summary>
[TestFixture]
public class FakeRedisSmokeTests
{
    [Test]
    public async Task StringSetThenGet_RoundTrips()
    {
        var db = RedisTestFactory.Create().GetDatabase();

        await db.StringSetAsync("k", "v", TimeSpan.FromMinutes(1));
        var back = await db.StringGetAsync("k");

        Assert.That((string?)back, Is.EqualTo("v"));
    }

    [Test]
    public async Task StringSetThenGet_ThroughTwoSeparateGetDatabaseCalls_SharesState()
    {
        // Every ledger's `Db` property calls redis.GetDatabase() fresh on each access - state must
        // survive that, not just a single cached IDatabase reference.
        var redis = RedisTestFactory.Create();

        await redis.GetDatabase().StringSetAsync("k", "v", TimeSpan.FromHours(1));
        var back = await redis.GetDatabase().StringGetAsync("k");

        Assert.That((string?)back, Is.EqualTo("v"));
    }
}
