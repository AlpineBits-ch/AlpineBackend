using System.Globalization;
using System.Text;

namespace Discovery.Api.Services;

/// <summary>Opaque paging cursor over (score, listingId, now). The id breaks ties on equal scores;
/// now is frozen at first-page time so every later page in the session scores against the same
/// instant.</summary>
public static class FeedCursor
{
    public static string Encode(double score, string listingId, DateTimeOffset now) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes($"{score:R}|{listingId}|{now.ToUnixTimeMilliseconds()}"));

    /// <summary>False on anything malformed rather than throwing - a cursor arrives from a client,
    /// and a bad one must answer the first page, not a 500.</summary>
    public static bool TryDecode(string? cursor, out double score, out string listingId, out DateTimeOffset now)
    {
        score = 0;
        listingId = string.Empty;
        now = default;
        if (string.IsNullOrEmpty(cursor)) return false;

        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        var raw = Encoding.UTF8.GetString(bytes);

        // listingId sits between the two separators - split from the end first, since the trailing
        // "now" segment is the one guaranteed to contain no '|'.
        var lastSeparator = raw.LastIndexOf('|');
        if (lastSeparator <= 0 || lastSeparator == raw.Length - 1) return false;

        var head = raw[..lastSeparator];
        var nowPart = raw[(lastSeparator + 1)..];

        var firstSeparator = head.IndexOf('|');
        if (firstSeparator <= 0 || firstSeparator == head.Length - 1) return false;

        if (!double.TryParse(head[..firstSeparator], NumberStyles.Float, CultureInfo.InvariantCulture, out score))
            return false;

        if (!long.TryParse(nowPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nowMillis))
            return false;

        listingId = head[(firstSeparator + 1)..];
        now = DateTimeOffset.FromUnixTimeMilliseconds(nowMillis);
        return true;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => "",
            _ => throw new FormatException("Not a base64url string."),
        };
        return Convert.FromBase64String(padded);
    }
}
