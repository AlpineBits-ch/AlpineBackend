using Identity.Application.Dtos.Request;
using Identity.Application.Services;

namespace Identity.Tests.Services;

/// <summary>Validation of the suppressed-games payload, decided from the payload alone.</summary>
[TestFixture]
public class HiddenActivitiesInputTests
{
    private static HiddenActivityDto ById(string id) => new() { ApplicationId = id };
    private static HiddenActivityDto ByName(string name) => new() { Name = name };

    // ── Normal ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Parse_SplitsEntriesByWhichKeyTheyUse()
    {
        var result = HiddenActivitiesInput.Parse([ById("730"), ByName("Spotify"), ById("356875221078245376")]);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.ApplicationIds, Is.EquivalentTo(new[] { "730", "356875221078245376" }));
        Assert.That(result.Names, Is.EquivalentTo(new[] { "Spotify" }));
    }

    [Test]
    public void Parse_TrimsSurroundingWhitespace()
    {
        var result = HiddenActivitiesInput.Parse([ById("  730  "), ByName("  Spotify  ")]);

        Assert.That(result.ApplicationIds, Is.EquivalentTo(new[] { "730" }));
        Assert.That(result.Names, Is.EquivalentTo(new[] { "Spotify" }));
    }

    [TestCase(null)]
    public void Parse_NullList_IsAnEmptySuppressionSet(IReadOnlyList<HiddenActivityDto>? entries)
    {
        var result = HiddenActivitiesInput.Parse(entries);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.ApplicationIds, Is.Empty);
        Assert.That(result.Names, Is.Empty);
    }

    [Test]
    public void Parse_EmptyList_ClearsRatherThanFailing()
    {
        var result = HiddenActivitiesInput.Parse([]);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.ApplicationIds, Is.Empty);
    }

    // ── Duplicates ──────────────────────────────────────────────────────────────────────────

    [Test]
    public void Parse_DuplicateApplicationId_IsCollapsedNotRejected()
    {
        // The client sends what its list holds; the same game twice is something to collapse, not
        // an error to hand back to a user who cannot act on it.
        var result = HiddenActivitiesInput.Parse([ById("730"), ById("730")]);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.ApplicationIds, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_NamesDifferingOnlyByCase_CollapseToOne()
    {
        var result = HiddenActivitiesInput.Parse([ByName("Spotify"), ByName("SPOTIFY")]);

        Assert.That(result.Names, Has.Count.EqualTo(1));
    }

    // ── The exactly-one-key invariant ───────────────────────────────────────────────────────

    [Test]
    public void Parse_EntryWithBothKeys_IsRejected()
    {
        var result = HiddenActivitiesInput.Parse([new HiddenActivityDto { ApplicationId = "730", Name = "CS2" }]);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("exactly one"));
    }

    [Test]
    public void Parse_EntryWithNeitherKey_IsRejected()
    {
        // A row with no key matches every activity the account has - the exact opposite of a
        // suppression list.
        var result = HiddenActivitiesInput.Parse([new HiddenActivityDto()]);

        Assert.That(result.Ok, Is.False);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Parse_BlankValuesCountAsAbsent(string blank)
    {
        var result = HiddenActivitiesInput.Parse([new HiddenActivityDto { ApplicationId = blank, Name = blank }]);

        Assert.That(result.Ok, Is.False, "two blanks are two absent keys, not two present ones");
    }

    // ── Field validation ────────────────────────────────────────────────────────────────────

    [TestCase("not-a-snowflake")]
    [TestCase("730a")]
    [TestCase("-730")]
    [TestCase("7.30")]
    public void Parse_NonNumericApplicationId_IsRejected(string applicationId)
    {
        Assert.That(HiddenActivitiesInput.Parse([ById(applicationId)]).Ok, Is.False);
    }

    [Test]
    public void Parse_OverLongApplicationId_IsRejected()
    {
        var tooLong = new string('9', HiddenActivityLimits.MaxApplicationIdLength + 1);

        Assert.That(HiddenActivitiesInput.Parse([ById(tooLong)]).Ok, Is.False);
    }

    [Test]
    public void Parse_OverLongName_IsRejected()
    {
        var tooLong = new string('a', HiddenActivityLimits.MaxNameLength + 1);

        Assert.That(HiddenActivitiesInput.Parse([ByName(tooLong)]).Ok, Is.False);
    }

    [Test]
    public void Parse_NameAtExactlyTheLimit_IsAccepted()
    {
        var atLimit = new string('a', HiddenActivityLimits.MaxNameLength);

        Assert.That(HiddenActivitiesInput.Parse([ByName(atLimit)]).Ok, Is.True);
    }

    // ── Bounds ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Parse_MoreThanTheMaximum_IsRejected()
    {
        // The set rides inside every cached copy of the privacy record, read on every presence
        // broadcast, so it is bounded rather than left to grow.
        var tooMany = Enumerable.Range(0, HiddenActivityLimits.MaxEntries + 1)
            .Select(i => ById(i.ToString()))
            .ToList();

        var result = HiddenActivitiesInput.Parse(tooMany);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain(HiddenActivityLimits.MaxEntries.ToString()));
    }

    [Test]
    public void Parse_ExactlyTheMaximum_IsAccepted()
    {
        var atLimit = Enumerable.Range(0, HiddenActivityLimits.MaxEntries)
            .Select(i => ById(i.ToString()))
            .ToList();

        Assert.That(HiddenActivitiesInput.Parse(atLimit).Ok, Is.True);
    }
}
