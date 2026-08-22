using Discovery.Domain.Topics;

namespace Discovery.Tests.Topics;

[TestFixture]
public class TagSlugTests
{
    [Test]
    public void Punctuation_drops_without_leaving_a_separator() =>
        Assert.That(TagSlug.Normalize("D&D 5e"), Is.EqualTo("dd-5e"));

    [Test]
    public void Runs_of_whitespace_collapse_to_one_hyphen() =>
        Assert.That(TagSlug.Normalize("  Play  By   Post "), Is.EqualTo("play-by-post"));

    [Test]
    public void A_hyphen_separates_rather_than_dropping() =>
        Assert.That(TagSlug.Normalize("Sci-Fi Play-By-Post"), Is.EqualTo("sci-fi-play-by-post"));

    [Test]
    public void Combining_marks_are_stripped() =>
        Assert.That(TagSlug.Normalize("Pokemo\u0301n"), Is.EqualTo("pokemon"));

    [Test]
    public void Nothing_surviving_is_null_and_not_an_empty_string() =>
        Assert.That(TagSlug.Normalize("---"), Is.Null);

    [Test]
    public void Trailing_punctuation_leaves_no_trailing_hyphen() =>
        Assert.That(TagSlug.Normalize("West Marches!!!"), Is.EqualTo("west-marches"));

    [Test]
    public void Truncation_does_not_leave_a_trailing_hyphen()
    {
        // 47 a's then a separator puts the hyphen at index 47, inside the cut, so the guard
        // has something to trim. MaxLength a's would place it exactly on the boundary and the
        // slice would drop it for free.
        var slug = TagSlug.Normalize(new string('a', TagSlug.MaxLength - 1) + " tail");
        Assert.Multiple(() =>
        {
            Assert.That(slug!.Length, Is.LessThanOrEqualTo(TagSlug.MaxLength));
            Assert.That(slug, Does.Not.EndWith("-"));
        });
    }
}
