using System.Text;
using Discovery.Api.Services;

namespace Discovery.Tests.Services;

[TestFixture]
public class FeedCursorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void A_cursor_round_trips()
    {
        var cursor = FeedCursor.Encode(0.42, "disc_abc123", Now);
        var ok = FeedCursor.TryDecode(cursor, out var score, out var listingId, out var now);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(score, Is.EqualTo(0.42));
            Assert.That(listingId, Is.EqualTo("disc_abc123"));
            Assert.That(now, Is.EqualTo(Now));
        });
    }

    [Test]
    public void A_malformed_cursor_decodes_to_nothing_rather_than_throwing()
    {
        var oneSeparatorOnly = ToBase64Url("0.5|disc_x");
        var nonNumericScore = ToBase64Url($"not-a-number|disc_x|{Now.ToUnixTimeMilliseconds()}");
        var nonNumericNow = ToBase64Url("0.5|disc_x|not-a-timestamp");
        var emptyId = ToBase64Url($"0.5||{Now.ToUnixTimeMilliseconds()}");
        var emptyScore = ToBase64Url($"|disc_x|{Now.ToUnixTimeMilliseconds()}");
        var emptyNow = ToBase64Url("0.5|disc_x|");

        Assert.Multiple(() =>
        {
            Assert.That(FeedCursor.TryDecode(null, out _, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode("", out _, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode("%%%not-base64%%%", out _, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(oneSeparatorOnly, out _, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(nonNumericScore, out _, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(nonNumericNow, out _, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(emptyId, out _, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(emptyScore, out _, out _, out _), Is.False);
            Assert.That(FeedCursor.TryDecode(emptyNow, out _, out _, out _), Is.False);
        });
    }

    private static string ToBase64Url(string raw) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
