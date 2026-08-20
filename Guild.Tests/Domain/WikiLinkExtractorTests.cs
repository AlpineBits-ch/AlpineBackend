using Guild.Domain.Entity;

namespace Guild.Tests.Domain;

/// <summary>
/// What counts as an edge in the page graph: the wiki: links a body really points at, and the ones
/// that only look like links because they are inside a code sample.
/// </summary>
[TestFixture]
public class WikiLinkExtractorTests
{
    private const string SourceId = "wkpg_source";
    private const string TargetId = "wkpg_target";

    [Test]
    public void A_plain_link_is_extracted()
    {
        var links = WikiLinkExtractor.Extract($"See [the map]({WikiLinkExtractor.Scheme}{TargetId}).", SourceId);

        Assert.That(links, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(links[0].TargetPageId, Is.EqualTo(TargetId));
            Assert.That(links[0].HeadingId, Is.Null);
        });
    }

    [Test]
    public void A_link_to_a_heading_keeps_the_slug()
    {
        var links = WikiLinkExtractor.Extract($"[there](wiki:{TargetId}#the-siege)", SourceId);

        Assert.That(links, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(links[0].TargetPageId, Is.EqualTo(TargetId));
            Assert.That(links[0].HeadingId, Is.EqualTo("the-siege"));
        });
    }

    /// <summary>A page documenting the link syntax must not link to whatever it uses as an
    /// example.</summary>
    [Test]
    public void A_link_inside_a_fenced_code_block_is_not_a_link()
    {
        var content = $"""
            Write it like this:

            ```
            [there](wiki:{TargetId})
            ```
            """;

        Assert.That(WikiLinkExtractor.Extract(content, SourceId), Is.Empty);
    }

    [Test]
    public void A_link_inside_a_code_span_is_not_a_link()
    {
        Assert.That(WikiLinkExtractor.Extract($"Write `[there](wiki:{TargetId})` to link.", SourceId), Is.Empty);
    }

    [Test]
    public void A_page_does_not_link_to_itself()
    {
        Assert.That(WikiLinkExtractor.Extract($"[me](wiki:{SourceId})", SourceId), Is.Empty);
    }

    [Test]
    public void The_same_target_and_heading_twice_is_one_link()
    {
        var links = WikiLinkExtractor.Extract($"[a](wiki:{TargetId}) and [b](wiki:{TargetId})", SourceId);

        Assert.That(links, Has.Count.EqualTo(1));
    }

    /// <summary>Two links at different headings are two edges here; collapsing them to one row is
    /// the write path's job, not the parser's.</summary>
    [Test]
    public void The_same_target_at_two_headings_is_two_links()
    {
        var links = WikiLinkExtractor.Extract($"[a](wiki:{TargetId}#one) and [b](wiki:{TargetId}#two)", SourceId);

        Assert.That(links, Has.Count.EqualTo(2));
        Assert.That(links.Select(l => l.HeadingId), Is.EqualTo(new[] { "one", "two" }));
    }

    /// <summary>A red link: the target is what somebody is about to write.</summary>
    [Test]
    public void A_link_to_a_page_that_does_not_exist_is_still_a_link()
    {
        var links = WikiLinkExtractor.Extract("[the keep](wiki:wkpg_unwritten)", SourceId);

        Assert.That(links, Has.Count.EqualTo(1));
        Assert.That(links[0].TargetPageId, Is.EqualTo("wkpg_unwritten"));
    }

    [Test]
    public void An_ordinary_link_is_not_a_page_link()
    {
        Assert.That(WikiLinkExtractor.Extract("[x](https://example.com) [y](/channels/1)", SourceId), Is.Empty);
    }

    [Test]
    public void Order_is_first_appearance()
    {
        var links = WikiLinkExtractor.Extract("[b](wiki:wkpg_b) [a](wiki:wkpg_a) [b again](wiki:wkpg_b)", SourceId);

        Assert.That(links.Select(l => l.TargetPageId), Is.EqualTo(new[] { "wkpg_b", "wkpg_a" }));
    }

    [Test]
    public void An_empty_body_has_no_links()
    {
        Assert.That(WikiLinkExtractor.Extract("   ", SourceId), Is.Empty);
    }
}
