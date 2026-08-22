using Guild.Domain;

namespace Guild.Tests.Domain;

[TestFixture]
public class LanguageTagTests
{
    [TestCase("en", "en")]
    [TestCase("EN", "en")]
    [TestCase("pt-br", "pt-BR")]
    [TestCase("zh-hans", "zh-Hans")]
    [TestCase("de-CH", "de-CH")]
    public void A_well_formed_tag_normalizes_to_canonical_case(string input, string expected)
    {
        Assert.That(LanguageTag.Normalize(input), Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("e")]
    [TestCase("english!")]
    [TestCase("en_US")]
    [TestCase("toolongsubtag")]
    [TestCase(null)]
    public void A_malformed_tag_is_refused(string? input)
    {
        Assert.That(LanguageTag.Normalize(input), Is.Null);
    }

    [Test]
    public void The_primary_is_dropped_from_the_others()
    {
        var ok = LanguageTag.TryNormalizeSet("en", ["EN", "de"], out var primary, out var others, out _);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(primary, Is.EqualTo("en"));
            Assert.That(others, Is.EqualTo(new[] { "de" }));
        });
    }

    [Test]
    public void Duplicates_among_the_others_collapse()
    {
        LanguageTag.TryNormalizeSet("en", ["de", "DE", "fr"], out _, out var others, out _);

        Assert.That(others, Is.EqualTo(new[] { "de", "fr" }));
    }

    [Test]
    public void More_than_four_others_is_refused_by_name()
    {
        var ok = LanguageTag.TryNormalizeSet("en", ["de", "fr", "it", "es", "pl"],
            out _, out _, out var problem);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(problem, Does.Contain("4"));
        });
    }

    [Test]
    public void A_malformed_other_names_the_offender()
    {
        var ok = LanguageTag.TryNormalizeSet("en", ["de", "nope!"], out _, out _, out var problem);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(problem, Does.Contain("nope!"));
        });
    }
}
