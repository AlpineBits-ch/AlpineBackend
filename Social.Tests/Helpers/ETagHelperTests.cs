using Social.Api.Helpers;

namespace Social.Tests.Helpers;

public class ETagHelperTests
{
    [Test]
    public void Compute_is_stable_for_equal_bodies()
    {
        Assert.That(
            ETagHelper.Compute(new { name = "ada", id = 1 }),
            Is.EqualTo(ETagHelper.Compute(new { name = "ada", id = 1 })));
    }

    [Test]
    public void Compute_differs_for_different_bodies()
    {
        Assert.That(
            ETagHelper.Compute(new { name = "grace" }),
            Is.Not.EqualTo(ETagHelper.Compute(new { name = "ada" })));
    }

    [Test]
    public void Compute_is_quoted_as_the_header_grammar_requires()
    {
        var etag = ETagHelper.Compute(new { name = "ada" });
        Assert.That(etag, Does.StartWith("\""));
        Assert.That(etag, Does.EndWith("\""));
    }

    [Test]
    public void Matches_accepts_the_exact_validator()
    {
        var etag = ETagHelper.Compute(new { name = "ada" });
        Assert.That(ETagHelper.Matches(etag, etag), Is.True);
    }

    [Test]
    public void Matches_accepts_a_star()
    {
        Assert.That(ETagHelper.Matches("*", ETagHelper.Compute(new { name = "ada" })), Is.True);
    }

    [Test]
    public void Matches_accepts_one_of_a_list()
    {
        var etag = ETagHelper.Compute(new { name = "ada" });
        Assert.That(ETagHelper.Matches($"\"other\", {etag}", etag), Is.True);
    }

    [Test]
    public void Matches_rejects_a_different_validator()
    {
        Assert.That(ETagHelper.Matches("\"other\"", ETagHelper.Compute(new { name = "ada" })), Is.False);
    }

    [Test]
    public void Matches_rejects_when_the_header_is_absent()
    {
        Assert.That(ETagHelper.Matches(null, ETagHelper.Compute(new { name = "ada" })), Is.False);
    }
}
