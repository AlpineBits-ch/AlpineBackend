using Guild.Domain.Services;

namespace Guild.Tests.Domain;

/// <summary>
/// The matching that decides whether an ingredient is already in the fridge or already on the
/// shopping list.
/// </summary>
[TestFixture]
public class IngredientMatchTests
{
    // ══════════════════════════════════════════════════════════════════════════
    // Normalize - the awkward inputs a real recipe is written in
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Normalize_StripsQuantityUnitAndPlural()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IngredientMatch.Normalize("2 packs of onions"), Is.EqualTo("onion"));
            Assert.That(IngredientMatch.Normalize("a pinch of salt"), Is.EqualTo("salt"));
            Assert.That(IngredientMatch.Normalize("Onion"), Is.EqualTo("onion"));
            Assert.That(IngredientMatch.Normalize("onions"), Is.EqualTo("onion"));
        });
    }

    [Test]
    public void Normalize_ReadsAQuantityGluedToItsUnit()
    {
        // "500g" is one token to a splitter and two to a reader, so the unit has to be split off
        // before it can be recognised - otherwise "500g flour" normalizes to "500g flour" and never
        // matches the bag of flour in the cupboard.
        Assert.That(IngredientMatch.Normalize("500g plain flour"), Is.EqualTo("plain flour"));
    }

    [Test]
    public void Normalize_DropsPunctuationAndCase()
    {
        Assert.That(IngredientMatch.Normalize("  Extra-virgin olive oil!  "),
            Is.EqualTo("extra virgin olive oil"));
    }

    /// <summary>An empty match name would match nothing at all (see <see cref="Matches"/>), which
    /// for a line that is genuinely only a quantity is worse than keeping the words: the line would
    /// be silently unbuyable rather than merely unmatched.</summary>
    [Test]
    public void Normalize_ALineThatIsNothingButAQuantity_KeepsItsWords()
    {
        Assert.That(IngredientMatch.Normalize("2 cups"), Is.EqualTo("cup"));
    }

    /// <summary>The singularizer drops a trailing "s" and nothing else.</summary>
    [Test]
    public void Normalize_LeavesWordsThatMerelyEndInS()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IngredientMatch.Normalize("glass"), Is.EqualTo("glass"));
            Assert.That(IngredientMatch.Normalize("hummus"), Is.EqualTo("hummus"));
            Assert.That(IngredientMatch.Normalize("rice"), Is.EqualTo("rice"));
        });
    }

    [Test]
    public void Normalize_EmptyInputs_AreEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IngredientMatch.Normalize(null), Is.Empty);
            Assert.That(IngredientMatch.Normalize(""), Is.Empty);
            Assert.That(IngredientMatch.Normalize("   "), Is.Empty);
            Assert.That(IngredientMatch.Normalize("!!!"), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Matches
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Matches_TheSameThingWrittenTwoWays()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IngredientMatch.Matches("onion", "Onions"), Is.True);
            Assert.That(IngredientMatch.Matches("2 onions", "onion"), Is.True);
            Assert.That(IngredientMatch.Matches("onion", "spring onions"), Is.True,
                "containment is what lets a specific pantry label cover a generic ingredient");
        });
    }

    /// <summary>The whole point of the class.</summary>
    [Test]
    public void Matches_RefusesANearMiss()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IngredientMatch.Matches("tomato", "potato"), Is.False,
                "one letter apart is not evidence of anything");
            Assert.That(IngredientMatch.Matches("ham", "hammer"), Is.False,
                "raw substring containment would match these, which is why containment is by word");
            Assert.That(IngredientMatch.Matches("oil", "boiled potatoes"), Is.False);
            Assert.That(IngredientMatch.Matches("butter", "peanut butter cups"), Is.True,
                "and this one genuinely is contained, word for word");
        });
    }

    /// <summary>An ingredient the normalizer could not read matches nothing, so it is always
    /// bought. That is the safe direction: the alternative is an empty match name behaving like a
    /// wildcard and eating the list.</summary>
    [Test]
    public void Matches_AnEmptySideNeverMatches()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IngredientMatch.Matches(null, "onion"), Is.False);
            Assert.That(IngredientMatch.Matches("onion", ""), Is.False);
            Assert.That(IngredientMatch.Matches("", ""), Is.False);
        });
    }
}
