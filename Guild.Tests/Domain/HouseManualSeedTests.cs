using System.Text.RegularExpressions;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Domain.Services;

namespace Guild.Tests.Domain;

/// <summary>The seeded house manual.</summary>
[TestFixture]
public class HouseManualSeedTests
{
    private const string GuildId = "gild-1";
    private const string OwnerId = "user-owner";

    private static HouseManualSeed.HouseManual Seed() => HouseManualSeed.ForHousehold(GuildId, OwnerId);

    private static CreateGuildParams Params(GuildKind kind, bool skipDefaultChannels = false) => new()
    {
        Name = "The Flat",
        OwnerId = OwnerId,
        OwnerSearchValue = "OWNER",
        Kind = kind,
        SkipDefaultChannels = skipDefaultChannels,
    };

    // ══════════════════════════════════════════════════════════════════════════ What gets seeded
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void ForHousehold_SeedsTheSixStarterPages()
    {
        var manual = Seed();

        Assert.That(manual.Pages.Select(p => p.Title), Is.EqualTo(HouseManualSeed.PageTitles));
    }

    [Test]
    public void ForHousehold_PutsEveryPageInTheOneCategory()
    {
        var manual = Seed();

        Assert.Multiple(() =>
        {
            Assert.That(manual.Category.Name, Is.EqualTo("House manual"));
            Assert.That(manual.Category.GuildId, Is.EqualTo(GuildId));
            Assert.That(manual.Pages.Select(p => p.CategoryId),
                Is.All.EqualTo(manual.Category.Id));
            Assert.That(manual.Pages.Select(p => p.GuildId), Is.All.EqualTo(GuildId));
            Assert.That(manual.Wiki.GuildId, Is.EqualTo(GuildId));
        });
    }

    /// <summary>Every page carries its own first revision, because that is what
    /// <c>WikiPage.Create</c> does and a seeded page with no revision would show a blank history the
    /// first time somebody edited it.</summary>
    [Test]
    public void ForHousehold_EveryPageIsAuthoredAndHasARevision()
    {
        var manual = Seed();

        Assert.Multiple(() =>
        {
            Assert.That(manual.Pages.Select(p => p.AuthorId), Is.All.EqualTo(OwnerId));
            Assert.That(manual.Pages.Select(p => p.Revisions.Count), Is.All.EqualTo(1));
        });
    }

    /// <summary>The map, pinned; the reference pages, not.</summary>
    [Test]
    public void ForHousehold_PinsOnlyTheOrientationPage()
    {
        var pinned = Seed().Pages.Where(p => p.IsPinned).Select(p => p.Title);

        Assert.That(pinned, Is.EqualTo(new[] { "How this house works" }));
    }

    [Test]
    public void ForHousehold_Rows_CarriesEverythingTheCallerHasToPersist()
    {
        var manual = Seed();

        Assert.That(manual.Rows, Has.Count.EqualTo(2 + manual.Pages.Count));
    }

    // ══════════════════════════════════════════════════════════════════════════ What must not be
    // in there ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void ForHousehold_EveryPageIsObviouslyABlankToFillIn()
    {
        foreach (var page in Seed().Pages)
        {
            Assert.That(page.Content, Does.Contain("["),
                $"'{page.Title}' has no bracketed prompt, so it does not read as a blank");
            Assert.That(page.Content.Trim(), Is.Not.Empty);
        }
    }

    /// <summary>No numbers at all.</summary>
    [Test]
    public void ForHousehold_InventsNoNumbers()
    {
        foreach (var page in Seed().Pages)
        {
            Assert.That(Regex.IsMatch(page.Content, @"\d{3,}"), Is.False,
                $"'{page.Title}' contains something that looks like a real number");
        }
    }

    /// <summary>Nor an email address, nor a URL somebody might click. Both read as answers.</summary>
    [Test]
    public void ForHousehold_InventsNoContactDetails()
    {
        foreach (var page in Seed().Pages)
        {
            Assert.Multiple(() =>
            {
                Assert.That(page.Content, Does.Not.Contain("@"), $"'{page.Title}' has an email address in it");
                Assert.That(page.Content, Does.Not.Contain("http"), $"'{page.Title}' has a link in it");
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════ When it is seeded
    // at all ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void HouseManualFor_AHousehold_GetsAManual()
    {
        var parameters = Params(GuildKind.Household);
        var guild = Guild.Domain.Aggregates.Guild.Create(parameters);

        var manual = Guild.Domain.Aggregates.Guild.HouseManualFor(guild, parameters);

        Assert.Multiple(() =>
        {
            Assert.That(manual, Is.Not.Null);
            Assert.That(manual!.Wiki.GuildId, Is.EqualTo(guild.Id));
            Assert.That(manual.Pages, Has.Count.EqualTo(HouseManualSeed.PageTitles.Count));
        });
    }

    [Test]
    public void HouseManualFor_ACommunityGuild_GetsNothing()
    {
        var parameters = Params(GuildKind.Community);
        var guild = Guild.Domain.Aggregates.Guild.Create(parameters);

        Assert.That(Guild.Domain.Aggregates.Guild.HouseManualFor(guild, parameters), Is.Null,
            "a gaming server does not need a page about bin day");
    }

    /// <summary>Discord import skips the default channels and brings its own tree; it must not
    /// acquire a house manual it never asked for either.</summary>
    [Test]
    public void HouseManualFor_AnImport_GetsNothing()
    {
        var parameters = Params(GuildKind.Household, skipDefaultChannels: true);
        var guild = Guild.Domain.Aggregates.Guild.Create(parameters);

        Assert.That(Guild.Domain.Aggregates.Guild.HouseManualFor(guild, parameters), Is.Null);
    }
}
