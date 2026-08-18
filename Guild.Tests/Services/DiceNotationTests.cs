using Guild.Domain.Services;

namespace Guild.Tests.Services;

/// <summary>
/// The parser is the whole risk in server-rolled dice: it is pure, total, and takes untrusted text.
/// These cover each notation form, the arithmetic between terms, every bound that has to refuse
/// before anything is evaluated, and malformed input that must not throw.
/// </summary>
[TestFixture]
public class DiceNotationTests
{
    /// <summary>Hands out a fixed sequence of faces, so an expectation can name the outcome.</summary>
    private sealed class QueueRoller(params int[] faces) : IDieRoller
    {
        private int _index;

        public int Roll(int sides) => _index < faces.Length ? faces[_index++] : sides;
    }

    private static DiceRollOutcome Roll(string expression, params int[] faces)
    {
        var parsed = DiceNotationParser.Parse(expression);
        Assert.That(parsed.Ok, Is.True, parsed.Error);
        return DiceEvaluator.Evaluate(parsed.Terms, parsed.Normalized, new QueueRoller(faces));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Notation forms

    [Test]
    public void Parse_SimplePool_ReadsCountAndSides()
    {
        var parsed = DiceNotationParser.Parse("2d6");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Ok, Is.True);
            Assert.That(parsed.Terms, Has.Count.EqualTo(1));
            Assert.That(parsed.Terms[0].Count, Is.EqualTo(2));
            Assert.That(parsed.Terms[0].Sides, Is.EqualTo(6));
        });
    }

    [Test]
    public void Parse_OmittedCount_IsOneDie()
    {
        var parsed = DiceNotationParser.Parse("d20");

        Assert.That(parsed.Terms[0].Count, Is.EqualTo(1));
    }

    [Test]
    public void Parse_KeepHighest_CarriesTheCount()
    {
        var parsed = DiceNotationParser.Parse("4d6kh3");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Terms[0].Keep, Is.EqualTo(DiceKeepMode.KeepHighest));
            Assert.That(parsed.Terms[0].KeepCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void Parse_DropWithNoCount_DropsOne()
    {
        var parsed = DiceNotationParser.Parse("4d6dl");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Terms[0].Keep, Is.EqualTo(DiceKeepMode.DropLowest));
            Assert.That(parsed.Terms[0].KeepCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Parse_AdvantageOnOneDie_RollsASecondOne()
    {
        var parsed = DiceNotationParser.Parse("1d20adv");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Terms[0].Count, Is.EqualTo(2), "advantage on a pool of one would change nothing");
            Assert.That(parsed.Terms[0].Keep, Is.EqualTo(DiceKeepMode.KeepHighest));
            Assert.That(parsed.Terms[0].KeepCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Parse_DisadvantageOnAPair_KeepsTheLowest()
    {
        var parsed = DiceNotationParser.Parse("2d20dis");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Terms[0].Count, Is.EqualTo(2));
            Assert.That(parsed.Terms[0].Keep, Is.EqualTo(DiceKeepMode.KeepLowest));
        });
    }

    [Test]
    public void Parse_Exploding_IsFlagged()
    {
        Assert.That(DiceNotationParser.Parse("1d10!").Terms[0].Explodes, Is.True);
    }

    [Test]
    public void Parse_ArithmeticBetweenTerms_KeepsSigns()
    {
        var parsed = DiceNotationParser.Parse("3d6+2d4-1");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Terms, Has.Count.EqualTo(3));
            Assert.That(parsed.Terms[1].Sign, Is.EqualTo(1));
            Assert.That(parsed.Terms[2].Sign, Is.EqualTo(-1));
            Assert.That(parsed.Terms[2].Constant, Is.EqualTo(1));
        });
    }

    [Test]
    public void Parse_LeadingMinus_AppliesToTheFirstTerm()
    {
        Assert.That(DiceNotationParser.Parse("-1d4+2").Terms[0].Sign, Is.EqualTo(-1));
    }

    [Test]
    public void Parse_WhitespaceAndCase_AreIgnored()
    {
        var spaced = DiceNotationParser.Parse(" 4D6 KH3 + 2 ");

        Assert.Multiple(() =>
        {
            Assert.That(spaced.Ok, Is.True, spaced.Error);
            Assert.That(spaced.Normalized, Is.EqualTo("4d6kh3 + 2"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Bounds, all refused before anything is rolled

    [TestCase("999999d999999", TestName = "Parse_TheDenialOfServiceCase_IsRefused")]
    [TestCase("101d6", TestName = "Parse_TooManyDiceInOneTerm_IsRefused")]
    [TestCase("60d6+60d6", TestName = "Parse_TooManyDiceAcrossTerms_IsRefused")]
    [TestCase("1d1", TestName = "Parse_ADieThatAlwaysShowsItsMaximum_IsRefused")]
    [TestCase("1d1001", TestName = "Parse_ADieTooLarge_IsRefused")]
    [TestCase("99999999d6", TestName = "Parse_ACountTooLongToBeANumber_IsRefused")]
    [TestCase("4d6kh0", TestName = "Parse_KeepingNothing_IsRefused")]
    [TestCase("2d6dl2", TestName = "Parse_DroppingEveryDie_IsRefused")]
    [TestCase("2d6kh5", TestName = "Parse_KeepingMoreThanWasRolled_IsRefused")]
    public void Parse_OutOfBounds_IsRefusedWithAReason(string expression)
    {
        var parsed = DiceNotationParser.Parse(expression);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Ok, Is.False);
            Assert.That(parsed.Error, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public void Parse_AnOverlongExpression_IsRefused()
    {
        var expression = string.Join('+', Enumerable.Repeat("1d6", 60));

        Assert.That(DiceNotationParser.Parse(expression).Ok, Is.False);
    }

    [Test]
    public void Parse_MoreTermsThanAllowed_IsRefused()
    {
        var expression = string.Join('+', Enumerable.Repeat("1", DiceLimits.MaxTerms + 1));

        Assert.That(DiceNotationParser.Parse(expression).Ok, Is.False);
    }

    [Test]
    public void Parse_AnOversizeConstant_IsRefused()
    {
        Assert.That(DiceNotationParser.Parse("9999999").Ok, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Malformed input

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("abc")]
    [TestCase("d")]
    [TestCase("3d")]
    [TestCase("+")]
    [TestCase("3d6+")]
    [TestCase("3d6++2")]
    [TestCase("3d6x2")]
    [TestCase("1d6.5")]
    [TestCase("1d6!!")]
    [TestCase("4d6kh3kl1")]
    [TestCase("2d20advdis")]
    [TestCase("2d20ad")]
    [TestCase("<script>alert(1)</script>")]
    public void Parse_Malformed_ReturnsARefusalRatherThanThrowing(string? expression)
    {
        var parsed = DiceNotationParser.Parse(expression);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Ok, Is.False);
            Assert.That(parsed.Terms, Is.Empty);
            Assert.That(parsed.Error, Is.Not.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Evaluation

    [Test]
    public void Evaluate_PoolPlusConstant_Totals()
    {
        var outcome = Roll("2d6+3", 4, 5);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Total, Is.EqualTo(12));
            Assert.That(outcome.Breakdown, Is.EqualTo("2d6 (4, 5) + 3"));
        });
    }

    [Test]
    public void Evaluate_KeepHighest_DropsTheRestAndShowsThem()
    {
        var outcome = Roll("4d6kh3", 6, 5, 3, 1);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Total, Is.EqualTo(14));
            Assert.That(outcome.Terms[0].Kept, Is.EqualTo(new[] { 6, 5, 3 }));
            Assert.That(outcome.Breakdown, Does.Contain("~1"), "seeing what was discarded is the point");
        });
    }

    [Test]
    public void Evaluate_DropLowest_KeepsEverythingElse()
    {
        var outcome = Roll("4d6dl1", 2, 6, 4, 5);

        Assert.That(outcome.Total, Is.EqualTo(15));
    }

    [Test]
    public void Evaluate_Advantage_KeepsTheHigherOfTwo()
    {
        var outcome = Roll("1d20adv", 7, 18);

        Assert.That(outcome.Total, Is.EqualTo(18));
    }

    [Test]
    public void Evaluate_Disadvantage_KeepsTheLowerOfTwo()
    {
        var outcome = Roll("1d20dis", 7, 18);

        Assert.That(outcome.Total, Is.EqualTo(7));
    }

    [Test]
    public void Evaluate_Exploding_AddsTheRerollIntoTheSameDie()
    {
        var outcome = Roll("1d10!", 10, 10, 4);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Total, Is.EqualTo(24));
            Assert.That(outcome.Terms[0].Rolls, Is.EqualTo(new[] { 10, 10, 4 }));
            Assert.That(outcome.Terms[0].Dice, Is.EqualTo(new[] { 24 }));
        });
    }

    [Test]
    public void Evaluate_ExplodingForever_StopsAtTheChainLimit()
    {
        // The queue runs out and the roller then always returns the maximum face, which is the
        // pathological case the chain limit exists for.
        var outcome = Roll("1d10!");

        Assert.That(outcome.Terms[0].Rolls, Has.Count.EqualTo(DiceLimits.MaxExplosionsPerDie + 1));
    }

    [Test]
    public void Evaluate_Subtraction_LowersTheTotal()
    {
        var outcome = Roll("3d6+2d4-1", 1, 2, 3, 4, 4);

        Assert.That(outcome.Total, Is.EqualTo(13));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The roller itself

    [Test]
    public void SecureRoller_OverManyRolls_ProducesEveryFaceAndNothingElse()
    {
        var roller = new SecureDieRoller();
        var seen = new int[7];

        for (var i = 0; i < 20_000; i++)
        {
            var face = roller.Roll(6);
            Assert.That(face, Is.InRange(1, 6));
            seen[face]++;
        }

        Assert.That(seen.Skip(1), Has.All.GreaterThan(0), "a d6 that never shows a face is not a d6");
    }
}
