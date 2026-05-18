using Guild.Domain.Entity;

namespace Guild.Tests.Domain;

[TestFixture]
public class WikiRevisionTests
{
    [Test]
    public void Create_SetsAllRequiredProperties()
    {
        var revision = WikiRevision.Create(new CreateWikiRevisionParams
        {
            PageId = "wkpg_abc",
            Content = "Revision content",
            EditorId = "user_xyz",
            RevisionNumber = 2,
        });

        Assert.Multiple(() =>
        {
            Assert.That(revision.PageId, Is.EqualTo("wkpg_abc"));
            Assert.That(revision.Content, Is.EqualTo("Revision content"));
            Assert.That(revision.EditorId, Is.EqualTo("user_xyz"));
            Assert.That(revision.RevisionNumber, Is.EqualTo(2));
            Assert.That(revision.Summary, Is.Null);
        });
    }

    [Test]
    public void Create_SetsSummary_WhenProvided()
    {
        var revision = WikiRevision.Create(new CreateWikiRevisionParams
        {
            PageId = "wkpg_abc",
            Content = "Body",
            EditorId = "user_xyz",
            RevisionNumber = 1,
            Summary = "Fixed typo",
        });

        Assert.That(revision.Summary, Is.EqualTo("Fixed typo"));
    }

    [Test]
    public void Create_GeneratesIdWithCorrectPrefix()
    {
        var revision = WikiRevision.Create(new CreateWikiRevisionParams
        {
            PageId = "wkpg_abc",
            Content = "Body",
            EditorId = "user_xyz",
            RevisionNumber = 1,
        });

        Assert.That(revision.Id, Does.StartWith("wkrv_"));
    }

    [Test]
    public void Create_SetsTimestamps()
    {
        var before = DateTimeOffset.UtcNow;
        var revision = WikiRevision.Create(new CreateWikiRevisionParams
        {
            PageId = "wkpg_abc",
            Content = "Body",
            EditorId = "user_xyz",
            RevisionNumber = 1,
        });
        var after = DateTimeOffset.UtcNow;

        Assert.Multiple(() =>
        {
            Assert.That(revision.CreatedAt, Is.GreaterThanOrEqualTo(before));
            Assert.That(revision.CreatedAt, Is.LessThanOrEqualTo(after));
            Assert.That(revision.UpdatedAt, Is.EqualTo(revision.CreatedAt));
        });
    }
}
