using System.Text;
using Messaging.Domain;

namespace Messaging.Tests.Domain;

/// <summary>
/// The measurement half of the ceiling: what "10,000 characters" means for text that is not ASCII.
/// </summary>
[TestFixture]
public class MessageLengthTests
{
    /// <summary>A grinning face, built from its code point so nothing in the tool chain can
    /// normalise it into something else.</summary>
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    /// <summary>Latin small e followed by a combining acute accent, which renders as one
    /// character and is deliberately not the precomposed code point.</summary>
    private static readonly string Combined = "e" + (char)0x0301;

    [Test]
    public void An_empty_or_absent_body_is_zero_characters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MessageLength.Of(null), Is.Zero);
            Assert.That(MessageLength.Of(string.Empty), Is.Zero);
        });
    }

    [Test]
    public void Ascii_counts_one_per_character()
    {
        Assert.That(MessageLength.Of(new string('a', 2000)), Is.EqualTo(2000));
    }

    /// <summary>
    /// The whole reason the count is not a byte count: the same sentence must cost the same wherever
    /// it is written.
    /// </summary>
    [Test]
    public void Cyrillic_and_cjk_are_not_charged_by_the_byte()
    {
        var cyrillic = new string([.. Enumerable.Range(0, 12).Select(i => (char)(0x0410 + i))]);
        var japanese = new string([.. Enumerable.Range(0, 7).Select(i => (char)(0x3042 + i))]);

        Assert.Multiple(() =>
        {
            Assert.That(MessageLength.Of(cyrillic), Is.EqualTo(12));
            Assert.That(Encoding.UTF8.GetByteCount(cyrillic), Is.EqualTo(24),
                "a byte count would charge this post twice over");

            Assert.That(MessageLength.Of(japanese), Is.EqualTo(7));
            Assert.That(Encoding.UTF8.GetByteCount(japanese), Is.EqualTo(21),
                "and this one three times over");
        });
    }

    /// <summary>A UTF-16 count is the other wrong answer, and the one C# hands you for free.</summary>
    [Test]
    public void An_astral_plane_character_is_one_character_not_two()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MessageLength.Of(Emoji), Is.EqualTo(1));
            Assert.That(Emoji.Length, Is.EqualTo(2), "string.Length is the answer this must not give");
        });
    }

    /// <summary>A combining sequence is one thing on the screen and is counted as one.</summary>
    [Test]
    public void A_grapheme_cluster_counts_once()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MessageLength.Of(Combined), Is.EqualTo(1));
            Assert.That(Combined.Length, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// A body made only of astral-plane characters is the cheapest way to find out whether the count
    /// is really per text element, because every wrong answer doubles.
    /// </summary>
    [Test]
    public void A_body_of_emoji_at_the_limit_measures_as_the_limit()
    {
        var body = string.Concat(Enumerable.Repeat(Emoji, 10_000));

        Assert.Multiple(() =>
        {
            Assert.That(MessageLength.Of(body), Is.EqualTo(10_000));
            Assert.That(body.Length, Is.EqualTo(20_000));
        });
    }
}
