using System.Text;
using Discovery.Api.Services;

namespace Discovery.Tests.Services;

[TestFixture]
public class FeedCursorTests
{
    [Test]
    public void A_cursor_round_trips()
    {
        var cursor = FeedCursor.Encode(0.42, "disc_abc123");
        var ok = FeedCursor.TryDecode(cursor, out var score, out var listingId);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(score, Is.EqualTo(0.42));
            Assert.That(listingId, Is.EqualTo("disc_abc123"));
        });
    }

    [Test]
    public void A_malformed_cursor_decodes_to_nothing_rather_than_throwing()
    {
        var noSeparator = ToBase64Url("nopipehere");
        var nonNumericScore = ToBase64Url("not-a-number|disc_x");
        var emptyId = ToBase64Url("0.5|");
        var emptyScore = ToBase64Url("|disc_x");

        Assert.Multiple(() =>
        {
            Assert.That(FeedCursor.TryDecode(null, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode("", out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode("%%%not-base64%%%", out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(noSeparator, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(nonNumericScore, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(emptyId, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(emptyScore, out _, out _), Is.False);
        });
    }

    [Test]
    public void The_id_breaks_ties_so_equal_scores_page_without_repeating()
    {
        // Same score, two different listings - without the id in the cursor these would be
        // indistinguishable, and paging past the first would either repeat or skip the second.
        var first = FeedCursor.Encode(0.5, "disc_a");
        var second = FeedCursor.Encode(0.5, "disc_b");

        FeedCursor.TryDecode(first, out var scoreA, out var idA);
        FeedCursor.TryDecode(second, out var scoreB, out var idB);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(scoreA, Is.EqualTo(scoreB));
            Assert.That(idA, Is.Not.EqualTo(idB));
        });
    }

    private static string ToBase64Url(string raw) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
