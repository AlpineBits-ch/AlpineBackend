using Guild.Domain.Entity;
using Guild.Domain.Events.Wiki;

namespace Guild.Tests.Domain;

[TestFixture]
public class WikiCategoryTests
{
    [Test]
    public void Create_SetsAllPropertiesFromParams()
    {
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "General",
            Position = 3,
        });

        Assert.Multiple(() =>
        {
            Assert.That(category.GuildId, Is.EqualTo("gild_abc"));
            Assert.That(category.Name, Is.EqualTo("General"));
            Assert.That(category.Position, Is.EqualTo(3));
        });
    }

    [Test]
    public void Create_GeneratesIdWithCorrectPrefix()
    {
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "Category",
            Position = 0,
        });

        Assert.That(category.Id, Does.StartWith("wkca_"));
    }

    [Test]
    public void Create_DefaultsToPositionZero()
    {
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "Category",
        });

        Assert.That(category.Position, Is.EqualTo(0));
    }

    [Test]
    public void Create_SetsTimestamps()
    {
        var before = DateTimeOffset.UtcNow;
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "Category",
        });
        var after = DateTimeOffset.UtcNow;

        Assert.Multiple(() =>
        {
            Assert.That(category.CreatedAt, Is.GreaterThanOrEqualTo(before));
            Assert.That(category.CreatedAt, Is.LessThanOrEqualTo(after));
            Assert.That(category.UpdatedAt, Is.EqualTo(category.CreatedAt));
        });
    }

    [Test]
    public void Create_TwoCategories_HaveDifferentIds()
    {
        var cat1 = WikiCategory.Create(new CreateWikiCategoryParams { GuildId = "gild_abc", Name = "A" });
        var cat2 = WikiCategory.Create(new CreateWikiCategoryParams { GuildId = "gild_abc", Name = "B" });

        Assert.That(cat1.Id, Is.Not.EqualTo(cat2.Id));
    }

    [Test]
    public void Create_WithParentCategoryId_SetsParentCategoryId()
    {
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "Child",
            ParentCategoryId = "wkca_parent",
        });

        Assert.That(category.ParentCategoryId, Is.EqualTo("wkca_parent"));
    }

    [Test]
    public void Create_WithoutParentCategoryId_ParentCategoryIdIsNull()
    {
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "Root",
        });

        Assert.That(category.ParentCategoryId, Is.Null);
    }

    [Test]
    public void Create_WithParentCategoryId_RaisesCreatedEventWithParentCategoryId()
    {
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "Child",
            ParentCategoryId = "wkca_parent",
        });

        var evt = category.GetDomainEvents().OfType<WikiCategoryCreated>().Single();
        Assert.That(evt.ParentCategoryId, Is.EqualTo("wkca_parent"));
    }

    [Test]
    public void Create_WithoutParentCategoryId_RaisesCreatedEventWithNullParentCategoryId()
    {
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "Root",
        });

        var evt = category.GetDomainEvents().OfType<WikiCategoryCreated>().Single();
        Assert.That(evt.ParentCategoryId, Is.Null);
    }

    [Test]
    public void RaiseUpdated_IncludesParentCategoryIdInEvent()
    {
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "Category",
            ParentCategoryId = "wkca_parent",
        });
        category.ClearDomainEvents();

        category.RaiseUpdated();

        var evt = category.GetDomainEvents().OfType<WikiCategoryUpdated>().Single();
        Assert.That(evt.ParentCategoryId, Is.EqualTo("wkca_parent"));
    }

    [Test]
    public void RaiseUpdated_AfterClearingParent_ParentCategoryIdIsNull()
    {
        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = "gild_abc",
            Name = "Category",
            ParentCategoryId = "wkca_parent",
        });
        category.ClearDomainEvents();
        category.ParentCategoryId = null;

        category.RaiseUpdated();

        var evt = category.GetDomainEvents().OfType<WikiCategoryUpdated>().Single();
        Assert.That(evt.ParentCategoryId, Is.Null);
    }
}
