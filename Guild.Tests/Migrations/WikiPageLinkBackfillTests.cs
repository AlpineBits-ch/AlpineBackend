using Guild.Domain.Entity;
using Guild.Persistence.Migrations;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Migrations;

/// <summary>
/// Covers 20260820140556_AddWikiPageLinks: the regex backfill that fills the graph for pages written
/// before the table existed.
/// </summary>
[TestFixture]
public class WikiPageLinkBackfillTests
{
    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();
        await MigrationSqlHarness.SeedGuildAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await PostgresTestDatabase.ResetAsync();

    [Test]
    public async Task Backfill_APlainLink_BecomesAnEdge()
    {
        var target = await SeedPageAsync("target", "nothing here");
        var source = await SeedPageAsync("source", $"See [the map](wiki:{target}).");

        await BackfillAsync();

        var links = await ReadLinksAsync();
        Assert.That(links, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(links[0].SourcePageId, Is.EqualTo(source));
            Assert.That(links[0].TargetPageId, Is.EqualTo(target));
            Assert.That(links[0].HeadingId, Is.Null);
            Assert.That(links[0].GuildId, Is.EqualTo(MigrationSqlHarness.GuildId));
        });
    }

    [Test]
    public async Task Backfill_ALinkToAHeading_KeepsTheSlug()
    {
        var target = await SeedPageAsync("target", "nothing here");
        await SeedPageAsync("source", $"[there](wiki:{target}#the-siege)");

        await BackfillAsync();

        var links = await ReadLinksAsync();
        Assert.That(links, Has.Count.EqualTo(1));
        Assert.That(links[0].HeadingId, Is.EqualTo("the-siege"));
    }

    [Test]
    public async Task Backfill_TheSameTargetTwice_IsOneRow()
    {
        var target = await SeedPageAsync("target", "nothing here");
        await SeedPageAsync("source", $"[a](wiki:{target}) and [b](wiki:{target}#later)");

        await BackfillAsync();

        Assert.That(await ReadLinksAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Backfill_ASelfLink_IsSkipped()
    {
        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = MigrationSqlHarness.GuildId, Title = "self", AuthorId = "author",
        });
        page.Content = $"[me](wiki:{page.Id})";

        await using (var context = new PostgresGuildContext())
        {
            context.WikiPages.Add(page);
            await context.SaveChangesAsync();
        }

        await BackfillAsync();

        Assert.That(await ReadLinksAsync(), Is.Empty);
    }

    /// <summary>The limitation this backfill is documented as having: SQL cannot tell a code sample
    /// from prose, and the page corrects itself the next time somebody saves it.</summary>
    [Test]
    public async Task Backfill_ALinkInsideACodeFence_IsMatchedAnyway()
    {
        var target = await SeedPageAsync("target", "nothing here");
        await SeedPageAsync("source", $"Write it like this:\n\n```\n[there](wiki:{target})\n```\n");

        await BackfillAsync();

        Assert.That(await ReadLinksAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Backfill_APageWithNoLinks_ProducesNothing()
    {
        await SeedPageAsync("source", "Just prose, and [an outside link](https://example.com).");

        await BackfillAsync();

        Assert.That(await ReadLinksAsync(), Is.Empty);
    }

    private static async Task<string> SeedPageAsync(string title, string content)
    {
        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = MigrationSqlHarness.GuildId, Title = title, Content = content, AuthorId = "author",
        });

        await using var context = new PostgresGuildContext();
        context.WikiPages.Add(page);
        await context.SaveChangesAsync();

        return page.Id;
    }

    private static async Task BackfillAsync()
    {
        await using var connection = await MigrationSqlHarness.OpenAsync();
        await MigrationSqlHarness.ExecuteAsync(connection, WikiPageLinkBackfill.ExtractExistingLinksSql);
    }

    private static async Task<List<WikiPageLink>> ReadLinksAsync()
    {
        await using var context = new PostgresGuildContext();
        return await context.WikiPageLinks.AsNoTracking()
            .OrderBy(l => l.SourcePageId)
            .ThenBy(l => l.TargetPageId)
            .ToListAsync();
    }
}
