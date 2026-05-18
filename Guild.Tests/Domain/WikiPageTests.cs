using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

[TestFixture]
public class WikiPageTests
{
    private static CreateWikiPageParams BasicParams(string title = "Hello World") => new()
    {
        GuildId = "gild_abc",
        Title = title,
        Content = "Some content",
        AuthorId = "user_xyz",
    };

    [Test]
    public void Create_SetsAllPropertiesFromParams()
    {
        var @params = new CreateWikiPageParams
        {
            GuildId = "gild_abc",
            Title = "My Page",
            Content = "Body text",
            AuthorId = "user_xyz",
            ParentPageId = "wkpg_parent",
            CategoryId = "wkca_cat1",
            Visibility = WikiVisibility.Private,
            Tags = ["alpha", "beta"],
            IsPinned = true,
        };

        var page = WikiPage.Create(@params);

        Assert.Multiple(() =>
        {
            Assert.That(page.GuildId, Is.EqualTo("gild_abc"));
            Assert.That(page.Title, Is.EqualTo("My Page"));
            Assert.That(page.Content, Is.EqualTo("Body text"));
            Assert.That(page.AuthorId, Is.EqualTo("user_xyz"));
            Assert.That(page.ParentPageId, Is.EqualTo("wkpg_parent"));
            Assert.That(page.CategoryId, Is.EqualTo("wkca_cat1"));
            Assert.That(page.Visibility, Is.EqualTo(WikiVisibility.Private));
            Assert.That(page.Tags, Is.EqualTo(new[] { "alpha", "beta" }));
            Assert.That(page.IsPinned, Is.True);
            Assert.That(page.LastEditorId, Is.Null);
        });
    }

    [Test]
    public void Create_GeneratesIdWithCorrectPrefix()
    {
        var page = WikiPage.Create(BasicParams());

        Assert.That(page.Id, Does.StartWith("wkpg_"));
    }

    [Test]
    public void Create_DefaultsToPublicVisibility()
    {
        var page = WikiPage.Create(BasicParams());

        Assert.That(page.Visibility, Is.EqualTo(WikiVisibility.Public));
    }

    [Test]
    public void Create_DefaultsToNotPinned()
    {
        var page = WikiPage.Create(BasicParams());

        Assert.That(page.IsPinned, Is.False);
    }

    [Test]
    public void Create_DefaultsToEmptyTags()
    {
        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = "gild_abc",
            Title = "Page",
            Content = "Body",
            AuthorId = "user_xyz",
        });

        Assert.That(page.Tags, Is.Empty);
    }

    [Test]
    public void Create_SetsTimestamps()
    {
        var before = DateTimeOffset.UtcNow;
        var page = WikiPage.Create(BasicParams());
        var after = DateTimeOffset.UtcNow;

        Assert.Multiple(() =>
        {
            Assert.That(page.CreatedAt, Is.GreaterThanOrEqualTo(before));
            Assert.That(page.CreatedAt, Is.LessThanOrEqualTo(after));
            Assert.That(page.UpdatedAt, Is.EqualTo(page.CreatedAt));
        });
    }

    // ── Slug generation ───────────────────────────────────────────────────────

    [Test]
    public void Create_SlugIsLowercase()
    {
        var page = WikiPage.Create(BasicParams("Hello World"));

        Assert.That(page.Slug, Is.EqualTo(page.Slug.ToLowerInvariant()));
    }

    [Test]
    public void Create_SlugReplacesSpacesWithHyphens()
    {
        var page = WikiPage.Create(BasicParams("Hello World"));

        Assert.That(page.Slug, Does.StartWith("hello-world"));
    }

    [Test]
    public void Create_SlugStripsSpecialCharacters()
    {
        var page = WikiPage.Create(BasicParams("Special! @#$ Page"));

        Assert.That(page.Slug, Does.Not.Contain("!"));
        Assert.That(page.Slug, Does.Not.Contain("@"));
        Assert.That(page.Slug, Does.Not.Contain("#"));
        Assert.That(page.Slug, Does.Not.Contain("$"));
    }

    [Test]
    public void Create_SlugContainsIdSuffix_ForUniqueness()
    {
        var page = WikiPage.Create(BasicParams("My Title"));
        var idSuffix = page.Id[^8..].ToLowerInvariant();

        Assert.That(page.Slug, Does.EndWith(idSuffix));
    }

    [Test]
    public void Create_TwoPages_WithSameTitle_ProduceDifferentSlugs()
    {
        var page1 = WikiPage.Create(BasicParams("Duplicate Title"));
        var page2 = WikiPage.Create(BasicParams("Duplicate Title"));

        Assert.That(page1.Slug, Is.Not.EqualTo(page2.Slug));
    }

    // ── Initial revision ──────────────────────────────────────────────────────

    [Test]
    public void Create_CreatesExactlyOneInitialRevision()
    {
        var page = WikiPage.Create(BasicParams());

        Assert.That(page.Revisions, Has.Count.EqualTo(1));
    }

    [Test]
    public void Create_InitialRevisionHasRevisionNumberOne()
    {
        var page = WikiPage.Create(BasicParams());

        Assert.That(page.Revisions.Single().RevisionNumber, Is.EqualTo(1));
    }

    [Test]
    public void Create_InitialRevisionContentMatchesPageContent()
    {
        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = "gild_abc",
            Title = "Title",
            Content = "Initial body",
            AuthorId = "user_xyz",
        });

        Assert.That(page.Revisions.Single().Content, Is.EqualTo("Initial body"));
    }

    [Test]
    public void Create_InitialRevisionEditorMatchesAuthor()
    {
        var page = WikiPage.Create(BasicParams());

        Assert.That(page.Revisions.Single().EditorId, Is.EqualTo(page.AuthorId));
    }

    [Test]
    public void Create_InitialRevisionPageIdMatchesPageId()
    {
        var page = WikiPage.Create(BasicParams());

        Assert.That(page.Revisions.Single().PageId, Is.EqualTo(page.Id));
    }

    [Test]
    public void Create_InitialRevisionHasNullSummary()
    {
        var page = WikiPage.Create(BasicParams());

        Assert.That(page.Revisions.Single().Summary, Is.Null);
    }

    [Test]
    public void Create_EmptyContent_StillCreatesRevision()
    {
        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = "gild_abc",
            Title = "Page",
            Content = string.Empty,
            AuthorId = "user_xyz",
        });

        Assert.That(page.Revisions, Has.Count.EqualTo(1));
        Assert.That(page.Revisions.Single().Content, Is.EqualTo(string.Empty));
    }
}
