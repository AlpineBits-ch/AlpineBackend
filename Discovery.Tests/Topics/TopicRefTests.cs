using Discovery.Domain.Topics;

namespace Discovery.Tests.Topics;

[TestFixture]
public class TopicRefTests
{
    [Test]
    public void Parses_a_game_reference()
    {
        var topic = TopicRef.Parse("game:gapp_01ABC");
        Assert.Multiple(() =>
        {
            Assert.That(topic.Kind, Is.EqualTo(TopicKind.Game));
            Assert.That(topic.Id, Is.EqualTo("gapp_01ABC"));
        });
    }

    [Test]
    public void A_tag_reference_normalizes_its_id()
    {
        var topic = TopicRef.Parse("tag:D&D 5e");
        Assert.That(topic.Id, Is.EqualTo("dd-5e"));
    }

    [Test]
    public void An_unknown_kind_does_not_parse() =>
        Assert.That(TopicRef.TryParse("guild:g1", out _), Is.False);

    [Test]
    public void A_tag_that_normalizes_to_nothing_does_not_parse() =>
        Assert.That(TopicRef.TryParse("tag:---", out _), Is.False);
}
