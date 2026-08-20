using Guild.Application.Services;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>
/// Guards <see cref="ChannelPrivacyService.BuildDenyPermissionsQuery"/> against the numeric
/// bitwise-and trap: EF happily translates <c>&amp;</c> between a ulong-backed <c>Permissions</c>
/// column and a literal into SQL, and Postgres only rejects it at execution ("42883: operator
/// does not exist: numeric &amp; numeric"). <c>ToQueryString</c> never contacts a server, so it
/// cannot reproduce that failure - it happily renders the broken SQL text too. What it can assert
/// is that the query this service actually sends contains no such operator, which is what would
/// break if the bit test were ever pushed back into the predicate.
/// </summary>
[TestFixture]
public class ChannelPrivacyQueryTranslationTests
{
    private PostgresGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new PostgresGuildContext();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public void BuildDenyPermissionsQuery_translates_without_a_numeric_bitwise_operator()
    {
        var sql = ChannelPrivacyService
            .BuildDenyPermissionsQuery(_context, "chan-1", "gild-1").ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("channel_permissions"));
            Assert.That(sql, Does.Not.Contain("&"),
                "the deny mask must come back untested and be bit-tested in memory; " +
                "Postgres has no & operator for numeric");
        });
    }
}
