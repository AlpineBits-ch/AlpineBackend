using System.Globalization;
using System.Text;

namespace Discovery.Api.Services;

/// <summary>
/// Opaque paging cursor over (score, listingId). The id breaks ties: score alone repeats or skips
/// rows whenever two listings tie, which happens constantly at zero interest overlap.
/// </summary>
public static class FeedCursor
{
    public static string Encode(double score, string listingId) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes($"{score:R}|{listingId}"));

    /// <summary>False on anything malformed rather than throwing - a cursor arrives from a client,
    /// and a bad one must answer the first page, not a 500.</summary>
    public static bool TryDecode(string? cursor, out double score, out string listingId)
    {
        score = 0;
        listingId = string.Empty;
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
        var separator = raw.IndexOf('|');
        if (separator <= 0 || separator == raw.Length - 1) return false;

        if (!double.TryParse(raw[..separator], NumberStyles.Float, CultureInfo.InvariantCulture, out score))
            return false;

        listingId = raw[(separator + 1)..];
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
