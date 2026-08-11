using Facet.Extensions;
using Guild.Application.Dtos.Response;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>
/// What the joined-guilds list (<c>GET /api/v1/guilds</c>) actually asks Postgres for.
/// </summary>
[TestFixture]
public class GuildListQueryTranslationTests
{
    private PostgresGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new PostgresGuildContext();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// The query as GuildController.GetGuilds builds it.
    private IQueryable<global::Guild.Domain.Aggregates.Guild> JoinedGuilds(string userId) => _context.Guilds
        .Include(g => g.Channels.OrderBy(c => c.CreatedAt))
        .Include(g => g.Roles.OrderBy(r => r.Position))
        .Include(g => g.Categories.OrderBy(c => c.CreatedAt))
        .Where(g => g.Members.Any(m => m.UserId == userId))
        .AsNoTracking();

    [Test]
    public void The_list_query_fetches_channels_categories_and_roles()
    {
        var sql = JoinedGuilds("user-1").ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("JOIN channels"), "a guild list without channels is a hollow list");
            Assert.That(sql, Does.Contain("JOIN categories"));
            Assert.That(sql, Does.Contain("JOIN roles"));
            // Membership is the filter, and it must be part of the SQL rather than applied after -
            // this endpoint returns every channel name of every guild it lists.
            Assert.That(sql, Does.Contain("guild_members"));
        });
    }

    /// <summary>
    /// Mapping with the untranslatable <c>ToFacet</c> must not quietly change the query.
    /// </summary>
    [Test]
    public void Mapping_to_the_dto_does_not_change_the_sql()
    {
        var included = JoinedGuilds("user-1");
        var projected = included.Select(g => g.ToFacet<global::Guild.Domain.Aggregates.Guild, GuildDto>());

        Assert.That(projected.ToQueryString(), Is.EqualTo(included.ToQueryString()));
    }
}
